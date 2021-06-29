module WTrace.Dotnet.Events.EFCore

open System
open System.Collections.Generic
open Microsoft.Diagnostics.Tracing
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons

type private EFCoreHandlerState =
    { Broadcast: EventBroadcast
      ProcessName: string
      ActionsCache: DataCache<string * string, float> }

[<AutoOpen>]
module private H =

    let activityKeywords =
        [| ("CommandExecuting", "CommandExecuted") |]

    let handleDiagnosticSourceEventSourceEvent id state (ev: EtwEvent) =
        if ev.PayloadCast<string>("SourceName") = "Microsoft.EntityFrameworkCore" then
            let activityId = getActivityId ev
            let eventName = ev.PayloadCast<string>("EventName")

            let details =
                ev.PayloadCast<array<IDictionary<string, obj>>>("Arguments")
                |> Array.map (fun d -> sprintf "%s: %s" (d.["Key"] :?> string) (d.["Value"] :?> string))
                |> String.concat ", "

            let details =
                match tryFindindMatchingActivity activityKeywords eventName activityId with
                | None -> details
                | Some (afterEventName, true) ->
                    // Some events could be recursive (example: Before/AfterViewPage) - I should have
                    // used a stack to track the flow, but I make the below simplification, so parent
                    // activities won't have duration
                    state.ActionsCache.[(activityId, afterEventName)] <- ev.TimeStampRelativeMSec
                    details // begin
                | Some (afterEventName, false) ->
                    let key = (activityId, afterEventName)

                    match state.ActionsCache.TryGetValue(key) with
                    | (true, ts) when details = "" ->
                        state.ActionsCache.Remove(key) |> ignore
                        $"duration: %.3f{ev.TimeStampRelativeMSec - ts}ms"
                    | (true, ts) ->
                        state.ActionsCache.Remove(key) |> ignore
                        $"%s{details}, duration: %.3f{ev.TimeStampRelativeMSec - ts}ms"
                    | (false, _) -> details

            let ev =
                { EventId = id
                  TimeStamp = ev.TimeStamp
                  ActivityId = activityId
                  ProcessId = ev.ProcessID
                  ProcessName = state.ProcessName
                  ThreadId = ev.ThreadID
                  EventName = $"EFCore/%s{eventName}"
                  EventLevel = t2e ev.Level
                  Path = ""
                  Details = details
                  Result = 0 }

            state.Broadcast ev

    let handleEFCoreEvent id state (ev: EtwEvent) =
        Debug.Assert((ev.ProviderName = DiagnosticSourceEventSourceName))
        handleDiagnosticSourceEventSourceEvent id state ev

    let isEventAccepted providerName eventName =
        if providerName = DiagnosticSourceEventSourceName then
            if eventName = "Event"
               || eventName = "Activity2/Start"
               || eventName = "Activity2/Stop" then
                EventFilterResponse.AcceptEvent
            else
                EventFilterResponse.RejectEvent
        else
            EventFilterResponse.RejectProvider

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> EFCoreHandlerState

        let predicate =
            Func<string, string, EventFilterResponse>(isEventAccepted)

        let handleEvent h =
            Action<_>(handleEvent session.EventLevel idgen state h)

        session.Dynamic.AddCallbackForProviderEvents(predicate, handleEvent handleEFCoreEvent)

    let getProviderSpec lvl =
        { Providers = Array.empty
          // taken from the DiagnosticSourceEventSource shortcut
          DiagnosticSourceFilterAndPayloadSpecs =
              [| "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting@Activity2Start:-"
                 + "Command.Connection.DataSource;"
                 + "Command.Connection.Database;"
                 + "Command.CommandText"
                 "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted@Activity2Stop:-" |]
          ExtensionLoggingFilterSpecs = Array.empty }


let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { Broadcast = broadcast
                ProcessName = proc.ProcessName
                ActionsCache = DataCache<_, _>(20) }
              :> obj
      Subscribe = subscribe }
