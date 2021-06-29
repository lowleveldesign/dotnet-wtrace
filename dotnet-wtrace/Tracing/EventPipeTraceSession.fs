namespace WTrace.Dotnet.Tracing

open System
open System.IO
open System.Diagnostics.Tracing
open System.Threading
open System.Reflection
open System.Threading.Tasks
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.NETCore.Client
open WTrace.Dotnet
open WTrace.Dotnet.Events

type RealtimeTraceSessionSettings =
    { TargetProcess: ProcessInfo
      EventStream: Stream
      Handlers: array<EventPipeEventHandler>
      EventLevel: EventLevel }

// I could possibly use IObserver instead of these status
// events. However, I was hitting errors when some handlers
// were sending events after the OnComplete or OnError event,
// causing exceptions from the Reactive library.
type RealtimeSessionStatus =
    | SessionStopped of EventsLost: int32
    | SessionError of Messge: string

module EventPipeTraceSession =

    [<AutoOpen>]
    module private H =

        let logger = Logger.Tracing

    let collectProcessInfoFromRundownEvents (traceFile: string) (ct: CancellationToken) =
        use eventSource = new EventPipeEventSource(traceFile)

        let mutable procInfo =
            { ProcessName = "<unknown>"
              ProcessId = 0 }

        use _ctr =
            ct.Register(fun () -> eventSource.Dispose())

        try
            eventSource.Dynamic.AddCallbackForProviderEvent(
                "Microsoft-DotNETCore-EventPipe",
                "ProcessInfo",
                (fun ev ->
                    let cmdline =
                        ev.PayloadStringByName("CommandLine")
                        |? "<unknown>"

                    let args =
                        cmdline.Split([| '\t'; ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)

                    procInfo <-
                        { ProcessName = Path.GetFileNameWithoutExtension(args.[0])
                          ProcessId = ev.ProcessID })
            )

            eventSource.Process() |> ignore
        with
        | :? ObjectDisposedException -> ()
        | :? NullReferenceException -> () // could happen on cancellation

        procInfo

    let start settings publishStatus publishTraceEvent =

        let handlersWithStates =
            settings.Handlers
            |> Array.map (fun h -> (h, h.Initialize(settings.TargetProcess, publishTraceEvent)))

        try
            use eventSource =
                new EventPipeEventSource(settings.EventStream)

            // Very simple Id generator for the session. It is never accessed asynchronously so there is no
            // risk if we simply increment it
            let mutable eventId = 0

            let idgen () =
                eventId <- eventId + 1
                eventId

            let sessionState = WTraceEventSource(eventSource, settings.EventLevel)

            // Subscribe handlers to the trace session
            handlersWithStates
            |> Array.iter (fun (h, s) -> h.Subscribe(sessionState, idgen, s))

            eventSource.Process() |> ignore

            publishStatus (SessionStopped eventSource.EventsLost)

            logger.TraceInformation(
                $"[{(nameof EventPipeSession)}] EventPipe session completed, {eventSource.EventsLost} event(s) lost"
            )
        with ex ->
            publishStatus (SessionError $"'%s{ex.Message}' <%s{ex.GetType().FullName}>")
            logger.TraceError(ex)


module internal DiagnosticsClientPrivateApi =
    type ReversedServer =
        { start: unit -> unit
          accept: TimeSpan -> obj
          close: unit -> unit }

    let diagClientType = typedefof<DiagnosticsClient>

    let reversedServerType =
        diagClientType.Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.ReversedDiagnosticsServer")

    let serverStart =
        reversedServerType.GetMethod("Start", Array.empty<Type>)

    let serverAccept = reversedServerType.GetMethod("Accept")

    let serverDisposeAsync =
        reversedServerType.GetMethod("DisposeAsync")

    let ipcEndpointInfoType =
        diagClientType.Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.IpcEndpointInfo")

    let ipcEndpointInfoEndpointProperty =
        ipcEndpointInfoType.GetProperty("Endpoint")

    let ipcEndpointInfoProcessIdProperty =
        ipcEndpointInfoType.GetProperty("ProcessId")

    let clientResumeRuntime =
        typedefof<DiagnosticsClient>.GetMethod ("ResumeRuntime", BindingFlags.NonPublic ||| BindingFlags.Instance)

    let createReversedServer diagPortName =
        let server =
            Activator.CreateInstance(reversedServerType, [| diagPortName :> obj |])

        { start =
              fun () ->
                  serverStart.Invoke(server, Array.empty<obj>)
                  |> ignore
          accept = fun timeout -> serverAccept.Invoke(server, [| timeout |])
          close =
              fun () ->
                  (serverDisposeAsync.Invoke(server, Array.empty<obj>) :?> ValueTask)
                      .AsTask()
                      .Wait() }

    let resumeRuntime client =
        clientResumeRuntime.Invoke(client, Array.empty<obj>)
        |> ignore

    let rec waitForProcessToConnect server pid =
        let endpointInfo =
            server.accept (TimeSpan.FromSeconds(15.0))

        let endpointPid =
            ipcEndpointInfoProcessIdProperty.GetValue(endpointInfo) :?> int32

        if endpointPid = pid then
            let endpoint =
                ipcEndpointInfoEndpointProperty.GetValue(endpointInfo)

            Activator.CreateInstance(
                diagClientType,
                BindingFlags.NonPublic ||| BindingFlags.Instance,
                null,
                [| endpoint |],
                null
            )
            :?> DiagnosticsClient
        else
            waitForProcessToConnect server pid