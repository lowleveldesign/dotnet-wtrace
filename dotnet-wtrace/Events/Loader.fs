module WTrace.Dotnet.Events.Loader

open System
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Tracing.Parsers.Clr
open System.Diagnostics.Tracing

type private LoaderHandlerState =
    { ProcessName: string
      Broadcast: EventBroadcast
      AssemblyLoads: DataCache<string, float> }

[<AutoOpen>]
module private H =

    let handleDomainModuleLoadUnload id state (ev: DomainModuleLoadUnloadTraceData) =
        let moduleType =
            if isNull ev.ModuleNativePath then
                "native"
            else
                "managed"

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Loader/%s{ev.OpcodeName}"
              EventLevel = t2e ev.Level
              Path =
                  if isNull ev.ModuleNativePath then
                      ev.ModuleNativePath
                  else
                      ev.ModuleILPath
              Details = $"module type: %s{moduleType}"
              Result = 0 }

        state.Broadcast ev

    let handleKnownPathProbed id state (ev: KnownPathProbedTraceData) =
        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Loader/%s{ev.OpcodeName}"
              EventLevel = t2e ev.Level
              Path = ev.FilePath
              Details = $"source: %s{ev.Source.ToString()}, runtime: #%d{ev.ClrInstanceID}"
              Result = ev.Result }

        state.Broadcast ev

    let handleResolutionAttempted id state (ev: ResolutionAttemptedTraceData) =
        let errorMessage =
            if String.IsNullOrEmpty(ev.ErrorMessage) then
                ""
            else
                $"message: '%s{ev.ErrorMessage}', "

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Loader/%s{ev.OpcodeName}"
              EventLevel =
                  if errorMessage = "" then
                      t2e ev.Level
                  else
                      EventLevel.Error
              Path = ev.ResultAssemblyPath
              Details = $"%s{errorMessage}stage: %s{ev.Stage.ToString()}"
              Result = int32 ev.Result (* enum: ResolutionAttemptedResult *)  }

        state.Broadcast ev

    let handleAssemblyLoadStart id state (ev: AssemblyLoadStartTraceData) =
        state.AssemblyLoads.Add(ev.AssemblyName, ev.TimeStampRelativeMSec)

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Loader/AssemblyLoadStart"
              EventLevel = t2e ev.Level
              Path = ev.AssemblyPath
              Details = $"name: '%s{ev.AssemblyName}', requested by: '%s{ev.RequestingAssembly}'"
              Result = 0 }

        state.Broadcast ev

    let handleAssemblyLoadStop id state (ev: AssemblyLoadStopTraceData) =
        let duration =
            match state.AssemblyLoads.TryGetValue(ev.AssemblyName) with
            | (true, ts) ->
                state.AssemblyLoads.Remove(ev.AssemblyName)
                |> ignore

                ev.TimeStampRelativeMSec - ts
            | (false, _) -> 0.0

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Loader/AssemblyLoadFinished"
              EventLevel = t2e ev.Level
              Path = ev.AssemblyPath
              Details =
                  $"name: '%s{ev.AssemblyName}', requested by: '%s{ev.RequestingAssembly}', duration: %.3f{duration}ms"
              Result = if ev.Success then 0 else 1 }

        state.Broadcast ev

    let getProviderSpec _ =
        { Providers = [| EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0xCL) |]
          DiagnosticSourceFilterAndPayloadSpecs = Array.empty<string>
          ExtensionLoggingFilterSpecs = Array.empty<string> }

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> LoaderHandlerState
        let handleEvent h = Action<_>(handleEvent EventLevel.Informational idgen state h)

        session.Clr.add_LoaderDomainModuleLoad (handleEvent handleDomainModuleLoadUnload)
        session.Clr.add_AssemblyLoaderKnownPathProbed (handleEvent handleKnownPathProbed)
        session.Clr.add_AssemblyLoaderResolutionAttempted (handleEvent handleResolutionAttempted)
        session.Clr.add_AssemblyLoaderStart (handleEvent handleAssemblyLoadStart)
        session.Clr.add_AssemblyLoaderStop (handleEvent handleAssemblyLoadStop)

let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { ProcessName = proc.ProcessName
                Broadcast = broadcast
                AssemblyLoads = DataCache<_, _>(20) }
              :> obj
      Subscribe = subscribe }
