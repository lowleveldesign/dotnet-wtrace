module WTrace.Dotnet.Events.Default

open System
open System.Collections.Generic
open System.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Tracing.Parsers.Clr
open System.Diagnostics

type private DefaultHandlerState =
    { ProcessName: string
      Broadcast: EventBroadcast
      ExceptionsCache: DataCache<int32, Stack<string * float>> }

[<AutoOpen>]
module private H =

    let logger = Logger.Tracing

    let handleRuntimeStart id state (ev: RuntimeInformationTraceData) =
        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "Clr/RuntimeInfo"
              EventLevel = t2e ev.Level
              Path = ev.RuntimeDllPath
              Details =
                  sprintf
                      "sku: %s, CLR version: %d.%d.%d.%d, VM version: %d.%d.%d.%d"
                      (ev.Sku.ToString())
                      ev.BclMajorVersion
                      ev.BclMinorVersion
                      ev.BclBuildNumber
                      ev.BclQfeNumber
                      ev.VMMajorVersion
                      ev.VMMinorVersion
                      ev.VMBuildNumber
                      ev.VMQfeNumber
              Result = 0 }

        state.Broadcast ev

    let handleExceptionStart id state (ev: ExceptionTraceData) =
        let stack =
            match state.ExceptionsCache.TryGetValue(ev.ThreadID) with
            | (true, stack) -> stack
            | (false, _) ->
                let stack = Stack<string * float>()
                state.ExceptionsCache.[ev.ThreadID] <- stack
                stack

        stack.Push((ev.ExceptionType, ev.TimeStampRelativeMSec))

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Exception/Thrown"
              EventLevel = t2e ev.Level
              Path = ev.ExceptionType
              Details = $"message: '%s{ev.ExceptionMessage}', eip: 0x%x{ev.ExceptionEIP}"
              Result = 0 }

        state.Broadcast ev

    let handleExceptionStop id state (ev: EmptyTraceData) =
        let exctype, ts =
            match state.ExceptionsCache.TryGetValue(ev.ThreadID) with
            | (true, stack) when stack.Count > 0 ->
                if stack.Count = 1 then
                    state.ExceptionsCache.Remove(ev.ThreadID)
                    |> ignore

                stack.Pop()
            | _ -> ("<unknown>", 0.0)

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Exception/Handled"
              EventLevel = t2e ev.Level
              Path = exctype
              Details = $"duration: %.3f{ev.TimeStampRelativeMSec - ts}ms"
              Result = 0 }

        state.Broadcast ev

    let handleProcessInfo id state (ev: EtwEvent) =
        let cmdline =
            ev.PayloadCast<string>("CommandLine") |? ""

        let args =
            cmdline.Split([| '\t'; ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = ""
              ProcessId = ev.ProcessID
              ProcessName = ev.ProcessName
              ThreadId = ev.ThreadID
              EventName = "Clr/ProcessInfo"
              EventLevel = t2e ev.Level
              Path = args.[0]
              Details =
                  if args.Length > 1 then
                      $"args: '%s{args.[1]}'"
                  else
                      ""
              Result = 0 }

        state.Broadcast ev

    let handleDiagnosticSourceMetaEvent id state (ev: EtwEvent) =
        // only log information about the diag source (diagnostics purposes)
        Debug.Assert(ev.EventName = "Message")

        if logger.Switch.ShouldTrace(TraceEventType.Verbose) then
            logger.TraceVerbose(sprintf "[DiagnosticSource] %s" (ev.PayloadCast<string>("Message")))

    let getProviderSpec _ =
        { Providers =
              [|
                 // Exceptions
                 EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x8000L)
                 // Rundown events required to resolve .NET metadata
                 EventPipeProvider("Microsoft-Windows-DotNETRuntimeRundown", EventLevel.Informational, 0x0L)
                 // ProcessInfo event:
                 EventPipeProvider("Microsoft-DotNETCore-EventPipe", EventLevel.Informational, 0x0L)
                 // Enable activity IDs:
                 EventPipeProvider("System.Threading.Tasks.TplEventSource", EventLevel.Informational, 0x80L) |]
          DiagnosticSourceFilterAndPayloadSpecs = Array.empty<string>
          ExtensionLoggingFilterSpecs = Array.empty<string> }

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> DefaultHandlerState

        let handleEvent h =
            Action<_>(handleEvent EventLevel.Informational idgen state h)

        // The CLR runtime provider also emits the RuntimeStart event, but only for the new process.
        // When we attach to a running process, we won't see it. The Rundown RuntimeStart always works,
        // but we receive it at the end of the runtime session.
        session.ClrRundown.add_RuntimeStart (handleEvent handleRuntimeStart)
        session.Clr.add_ExceptionStart (handleEvent handleExceptionStart)
        session.Clr.add_ExceptionStop (handleEvent handleExceptionStop)

        session.Dynamic.AddCallbackForProviderEvent(
            "Microsoft-DotNETCore-EventPipe",
            "ProcessInfo",
            handleEvent handleProcessInfo
        )

        session.Dynamic.AddCallbackForProviderEvent(
            DiagnosticSourceEventSourceName,
            "Message",
            handleEvent handleDiagnosticSourceMetaEvent
        )

let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { ProcessName = proc.ProcessName
                Broadcast = broadcast
                ExceptionsCache = DataCache<_, _>(20) }
              :> obj
      Subscribe = subscribe }
