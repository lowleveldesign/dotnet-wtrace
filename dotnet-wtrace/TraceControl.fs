module WTrace.Dotnet.TraceControl

open System
open System.Diagnostics
open System.Diagnostics.Tracing
open System.Reactive.Linq
open System.Threading
open System.Collections.Generic
open System.IO
open Microsoft.Diagnostics.NETCore.Client
open FSharp.Control.Reactive
open WTrace.Dotnet.Events
open WTrace.Dotnet.Tracing

let mutable lostEventsCount = 0
let sessionWaitEvent = new ManualResetEvent(false)

[<AutoOpen>]
module private H =

    let emptyArgs = new Dictionary<string, string>()

    let logger = Logger.Tracing

    type Session =
        { TargetProcess: ProcessInfo
          EventStream: Stream
          stop: unit -> unit
          resume: unit -> unit
          waitForProcess: unit -> unit
          cleanup: unit -> unit }

    let updateStatus s =
        match s with
        | SessionError msg ->
            printfn "[ERROR] Error in the trace session: %s" msg
            sessionWaitEvent.Set() |> ignore
        | SessionStopped n ->
            lostEventsCount <- n
            sessionWaitEvent.Set() |> ignore

    let printHeader ev =
        let prevColor = Console.ForegroundColor

        try
            let eventColor =
                if (ev.EventName.StartsWith("Network/", StringComparison.Ordinal)) then
                    ConsoleColor.Blue
                elif (ev.EventName.StartsWith("Loader/", StringComparison.Ordinal)) then
                    ConsoleColor.Green
                elif (ev.EventName.StartsWith("Exception/", StringComparison.Ordinal)) then
                    ConsoleColor.DarkYellow
                elif (ev.EventName.StartsWith("GC/", StringComparison.Ordinal)) then
                    ConsoleColor.Magenta
                elif (ev.EventName.StartsWith("Clr/", StringComparison.Ordinal)) then
                    ConsoleColor.Gray
                elif (ev.EventName.StartsWith("AspNet/", StringComparison.Ordinal)) then
                    ConsoleColor.DarkCyan
                elif (ev.EventName.StartsWith("EFCore/", StringComparison.Ordinal)) then
                    ConsoleColor.Cyan
                else
                    prevColor

            let levelColor =
                match ev.EventLevel with
                | EventLevel.Critical
                | EventLevel.Error -> ConsoleColor.DarkRed
                | _ -> eventColor

            Console.ForegroundColor <- eventColor

            printf
                "%s %s (%d.%d) %s "
                (ev.TimeStamp.ToString("HH:mm:ss.ffff"))
                ev.ProcessName
                ev.ProcessId
                ev.ThreadId
                ev.EventName

            Console.ForegroundColor <- levelColor
            printf "%s" (EventLevel.toString ev.EventLevel)
        finally
            Console.ForegroundColor <- prevColor

    let printHeaderNoColor ev =
        printf
            "%s %s (%d.%d) %s %s"
            (ev.TimeStamp.ToString("HH:mm:ss.ffff"))
            ev.ProcessName
            ev.ProcessId
            ev.ThreadId
            ev.EventName
            (EventLevel.toString ev.EventLevel)

    let getPath v =
        if v = "" then "" else sprintf " '%s'" v

    let getActivity v =
        if v = "" then
            " []"
        else
            sprintf $" [%s{v}]"

    let getDesc v = if v = "" then "" else sprintf " %s" v

    let onEvent noColor (ev: TraceEvent) =
        if noColor then
            printHeaderNoColor ev
        else
            printHeader ev

        printfn "%s%s%s" (getActivity ev.ActivityId) (getPath ev.Path) (getDesc ev.Details)

    let providerFolder m (provider: EventPipeProvider) =
        let newp =
            match m |> Map.tryFind provider.Name with
            | None -> provider
            | Some (p: EventPipeProvider) ->
                let level =
                    if p.EventLevel < provider.EventLevel then
                        p.EventLevel
                    else
                        provider.EventLevel

                let keywords = p.Keywords ||| provider.Keywords

                EventPipeProvider(p.Name, level, keywords, emptyArgs)

        m |> Map.add newp.Name newp

    let prepareProviders eventLevel handlers =
        let providersMap =
            handlers
            |> Seq.collect (fun h -> (h.GetProviderSpec eventLevel).Providers)
            |> Seq.fold providerFolder Map.empty<string, EventPipeProvider>

        if providersMap
           |> Map.containsKey HandlerCommons.DiagnosticSourceEventSourceName then
            logger.TraceWarning "DiagnosticSourceEventSource defined as one of the handler providers - will be replaced"

        if providersMap
           |> Map.containsKey HandlerCommons.LoggingEventSourceName then
            logger.TraceWarning "LoggingEventSource defined as one of the handler providers - will be replaced"

        // DiagnosticSourceEventSource
        let filterAndPayloadSpecs =
            handlers
            |> Seq.collect
                (fun h ->
                    (h.GetProviderSpec eventLevel)
                        .DiagnosticSourceFilterAndPayloadSpecs)
            |> String.concat "\n"

        let providersMap =
            if filterAndPayloadSpecs <> "" then
                let eventsource =
                    EventPipeProvider(
                        HandlerCommons.DiagnosticSourceEventSourceName,
                        eventLevel,
                        0x803L, // 0x800 - disable shortcuts
                        [| "FilterAndPayloadSpecs", filterAndPayloadSpecs |]
                        |> dict
                    )

                providersMap
                |> Map.add eventsource.Name eventsource
            else
                providersMap

        // LoggingEventSource
        let filterAndPayloadSpecs =
            handlers
            |> Seq.collect
                (fun h ->
                    (h.GetProviderSpec eventLevel)
                        .ExtensionLoggingFilterSpecs)
            |> String.concat ";"

        let providersMap =
            if filterAndPayloadSpecs <> "" then
                let eventsource =
                    EventPipeProvider(
                        HandlerCommons.LoggingEventSourceName,
                        eventLevel,
                        0x4L,
                        [| "FilterSpecs", filterAndPayloadSpecs |] |> dict
                    )

                providersMap
                |> Map.add eventsource.Name eventsource
            else
                providersMap

        providersMap |> Map.toArray |> Array.map snd

    let createEventSubscription settings filters noColor =
        let filter = EventFilter.buildFilterFunction filters

        let sessionSubscribe (o: IObserver<TraceEvent>) (ct: CancellationToken) =
            async {
                // the ct token when cancelled should stop the trace session gracefully
                EventPipeTraceSession.start settings updateStatus o.OnNext
                return RxDisposable.Empty
            }
            |> Async.StartAsTask

        Observable.Create(sessionSubscribe)
        |> Observable.filter (filter)
        |> Observable.subscribe (onEvent noColor)

    // returns true if the process stopped by itself, false if the ct got cancelled
    let rec waitForProcessExit (ct: CancellationToken) (proc: Process) =
        if proc.HasExited then
            true
        elif ct.IsCancellationRequested then
            false
        elif sessionWaitEvent.WaitOne(0) then
            false
        elif proc.WaitForExit(200) then
            true
        else
            waitForProcessExit ct proc

    let rec waitForSession (ct: CancellationToken) =
        if ct.IsCancellationRequested then ()
        elif sessionWaitEvent.WaitOne(0) then ()
        else waitForSession ct

    let resumeProcess diagclient =
        DiagnosticsClientPrivateApi.resumeRuntime diagclient

    let openTraceFile path ct =
        try
            printf "Please wait. Analyzing rundown events... "

            let proc =
                EventPipeTraceSession.collectProcessInfoFromRundownEvents path ct

            printfn "done"

            let stream = File.OpenRead(path)

            Ok
                { TargetProcess = proc
                  EventStream = stream
                  stop = fun () -> sessionWaitEvent.Set() |> ignore
                  resume = id
                  waitForProcess = fun () -> waitForSession ct
                  cleanup = fun () -> stream.Dispose() }
        with ex -> Error(ex.ToString()) // ex.Message


    let startProcess newConsole (args: list<string>) (providers: array<EventPipeProvider>) ct =
        Debug.Assert(args.Length > 0, "[TraceControl] invalid number of arguments")

        let now = DateTime.Now.ToString("yyyyMMdd_HHmmss")

        let diagPortName =
            $"wtrace-dotnet-{Process.GetCurrentProcess().Id}-{now}.socket"

        let reversedServer =
            DiagnosticsClientPrivateApi.createReversedServer diagPortName

        try
            reversedServer.start ()

            let startInfo =
                ProcessStartInfo(
                    args.[0],
                    (args |> Seq.skip 1 |> String.concat " "),
                    UseShellExecute = false,
                    CreateNoWindow = not newConsole
                )

            startInfo.Environment.Add("DOTNET_DiagnosticPorts", diagPortName)

            let proc = Process.Start(startInfo)

            let diagclient =
                DiagnosticsClientPrivateApi.waitForProcessToConnect reversedServer proc.Id

            let session =
                diagclient.StartEventPipeSession(providers, true)

            Ok
                { TargetProcess =
                      { ProcessName = proc.ProcessName
                        ProcessId = proc.Id }
                  EventStream = session.EventStream
                  stop =
                      fun _ ->
                          try
                              session.Stop()
                          with :? ServerNotAvailableException -> ()
                  resume = fun () -> resumeProcess diagclient
                  waitForProcess =
                      fun () ->
                          if waitForProcessExit ct proc then
                              printfn $"Process ({proc.Id}) exited."
                  cleanup =
                      fun () ->
                          session.Dispose()
                          reversedServer.close () }
        with ex ->
            reversedServer.close ()
            Error ex.Message

    let traceRunningProcess pid (providers: array<EventPipeProvider>) ct =
        try
            let diagclient = DiagnosticsClient(pid)

            let session =
                diagclient.StartEventPipeSession(providers, true)

            let proc = Process.GetProcessById(pid)

            Ok
                { TargetProcess =
                      { ProcessName = proc.ProcessName
                        ProcessId = proc.Id }
                  EventStream = session.EventStream
                  stop = fun _ -> session.Stop()
                  resume = id
                  waitForProcess =
                      fun () ->
                          if waitForProcessExit ct proc then
                              printfn $"Process ({proc.Id}) exited."
                  cleanup = fun () -> session.Dispose() }
        with ex -> Error ex.Message

let startTrace traceTarget handlers filters level newConsole noColor (ct: CancellationToken) =
    result {
        let providers = prepareProviders level handlers

        if logger.Switch.ShouldTrace(TraceEventType.Verbose) then
            providers
            |> Array.iter
                (fun p ->
                    logger.TraceVerbose($"[TraceControl] Enabled %s{p.Name}:%x{p.Keywords}:%d{int32 p.EventLevel}")

                    if logger.Switch.ShouldTrace(TraceEventType.Verbose)
                       && not (isNull p.Arguments) && p.Arguments.Count > 0 then
                        p.Arguments
                        |> Seq.map (|KeyValue|)
                        |> Seq.iter (fun (k, v) -> logger.TraceVerbose($"[TraceControl] - %s{k}: %s{v}")))

        printfn ""
        printfn "Press Ctrl + C to stop the application."

        let! sess =
            match traceTarget with
            | RunningProcess pid -> traceRunningProcess pid providers ct
            | NewProcess args -> startProcess newConsole args providers ct
            | TraceFile path -> openTraceFile path ct

        try
            let settings =
                { TargetProcess = sess.TargetProcess
                  EventStream = sess.EventStream
                  Handlers = handlers
                  EventLevel = level }

            logger.TraceInformation($"[{(nameof EventPipeSession)}] Creating EventPipe subscription")

            use sub =
                createEventSubscription settings filters noColor

            use _ctr = ct.Register(fun () -> sess.stop ())

            sess.resume ()

            sess.waitForProcess ()

            if not (sessionWaitEvent.WaitOne(TimeSpan.FromSeconds(3.0))) then
                printfn "WARNING: the session did not finish in the allotted time."

            if lostEventsCount > 0 then
                printfn
                    "WARNING: %d events were lost in the session. Check wtrace help at https://wtrace.net to learn more."
                    lostEventsCount
        finally
            sess.cleanup ()
    }
