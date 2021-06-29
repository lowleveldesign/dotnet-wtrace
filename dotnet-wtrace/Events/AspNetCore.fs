module WTrace.Dotnet.Events.AspNetCore

open System
open System.Collections.Generic
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.NETCore.Client
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons

type private AspNetCoreHandlerState =
    { Broadcast: EventBroadcast
      ProcessName: string
      ActionsCache: DataCache<string * string, float> }

[<AutoOpen>]
module private H =

    let activityKeywords =
        [| ("Before", "After")
           ("Begin", "End") |]

    let handleDiagnosticSourceEventSourceEvent id state (ev: EtwEvent) =
        if ev.PayloadCast<string>("SourceName") = "Microsoft.AspNetCore" then
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
                  EventName = $"AspNet/%s{eventName}"
                  EventLevel = t2e ev.Level
                  Path = ""
                  Details = details
                  Result = 0 }

            state.Broadcast ev

    let handleKestrelEventSourceEvent id state (ev: EtwEvent) =
        Debug.Assert(ev.ProviderName = "Microsoft-AspNetCore-Server-Kestrel")

        let eventName, details =
            match ev.EventName with
            | "Connection/Start" -> "KestrelConnStart", sprintf "conn: %s, local addr: %s, remote addr: %s" (ev.PayloadCast<string>("connectionId")) (ev.PayloadCast<string>("localEndPoint")) (ev.PayloadCast<string>("remoteEndPoint"))
            | "Connection/Stop" -> "KestrelConnStop", sprintf "conn: %s" (ev.PayloadCast<string>("connectionId"))
            | "ConnectionRejected" -> "KestrelConnRejected", sprintf "conn: %s" (ev.PayloadCast<string>("connectionId"))
            | "Request/Start" ->
                "KestrelRequestStart", sprintf "conn: %s, req: %s, http: %s, path: %s, method: %s" (ev.PayloadCast<string>("connectionId")) (ev.PayloadCast<string>("requestId")) (ev.PayloadCast<string>("httpVersion")) (ev.PayloadCast<string>("path")) (ev.PayloadCast<string>("method"))
            | "Request/Stop" -> "KestrelRequestStop", sprintf "conn: %s, req: %s" (ev.PayloadCast<string>("connectionId")) (ev.PayloadCast<string>("requestId"))
            | "TlsHandshake/Start" -> "KestrelTlsStart", sprintf "conn: %s, tls: %s" (ev.PayloadCast<string>("connectionId")) (ev.PayloadCast<string>("sslProtocols"))
            | "TlsHandshake/Stop" -> "KestrelTlsStop", sprintf "conn: %s, tls: %s, app: %s, host: %s" (ev.PayloadCast<string>("connectionId")) (ev.PayloadCast<string>("sslProtocols")) (ev.PayloadCast<string>("applicationProtocols")) (ev.PayloadCast<string>("hostname"))
            | _ -> "<unknown>", $"%s{ev.EventName}"

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"AspNet/%s{eventName}"
              EventLevel = t2e ev.Level
              Path = ""
              Details = details
              Result = 0 }

        state.Broadcast ev

    let handleAspNetEvent id state (ev: EtwEvent) =
        if ev.ProviderName = DiagnosticSourceEventSourceName then
            handleDiagnosticSourceEventSourceEvent id state ev
        else
            handleKestrelEventSourceEvent id state ev

    let isEventAccepted providerName eventName =
        if providerName = DiagnosticSourceEventSourceName then
            if eventName = "Event"
               || eventName = "Activity1/Start"
               || eventName = "Activity1/Stop" then
                EventFilterResponse.AcceptEvent
            else
                EventFilterResponse.RejectEvent
        elif providerName = "Microsoft-AspNetCore-Server-Kestrel" then
            EventFilterResponse.AcceptEvent
        else
            EventFilterResponse.RejectProvider

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> AspNetCoreHandlerState

        let predicate =
            Func<string, string, EventFilterResponse>(isEventAccepted)

        let handleEvent h =
            Action<_>(handleEvent session.EventLevel idgen state h)

        session.Dynamic.AddCallbackForProviderEvents(predicate, handleEvent handleAspNetEvent)

    let getProviderSpec lvl =
        { Providers = [| EventPipeProvider("Microsoft-AspNetCore-Server-Kestrel", lvl, -1L) |]
          // taken from the DiagnosticSourceEventSource shortcut
          DiagnosticSourceFilterAndPayloadSpecs =
              [| "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.BeginRequest@Activity1Start:-"
                 + "httpContext.Request.Method;"
                 + "httpContext.Request.Host;"
                 + "httpContext.Request.Path;"
                 + "httpContext.Request.QueryString"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.EndRequest@Activity1Stop:-"
                 + "httpContext.TraceIdentifier;"
                 + "httpContext.Response.StatusCode"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeHandlerMethod:-"
                 + "HandlerMethodDescriptor.HttpMethod;"
                 + "HandlerMethodDescriptor.MethodInfo.Name"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterHandlerMethod:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnPageHandlerExecution:-"
                 + "ActionDescriptor.ModelTypeInfo;"
                 + "ActionDescriptor.HandlerTypeInfo;"
                 + "ActionDescriptor.PageTypeInfo"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnPageHandlerExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnPageHandlerExecuting:-"
                 + "ActionDescriptor.ModelTypeInfo;"
                 + "ActionDescriptor.HandlerTypeInfo;"
                 + "ActionDescriptor.PageTypeInfo"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnPageHandlerExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnPageHandlerExecuted:-"
                 + "ActionDescriptor.ModelTypeInfo;"
                 + "ActionDescriptor.HandlerTypeInfo;"
                 + "ActionDescriptor.PageTypeInfo"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnPageHandlerExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnPageHandlerSelection:-"
                 + "ActionDescriptor.ModelTypeInfo;"
                 + "ActionDescriptor.HandlerTypeInfo;"
                 + "ActionDescriptor.PageTypeInfo"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnPageHandlerSelection:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnPageHandlerSelected:-"
                 + "ActionDescriptor.ModelTypeInfo;"
                 + "ActionDescriptor.HandlerTypeInfo;"
                 + "ActionDescriptor.PageTypeInfo"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnPageHandlerSelected:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeAction:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterAction:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnAuthorization:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnAuthorization:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResourceExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResourceExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResourceExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResourceExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResourceExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResourceExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnException:-"
                 + "ExceptionContext.Exception;"
                 + "ExceptionContext.ExceptionHandled;"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnException:-"
                 + "ExceptionContext.Exception;"
                 + "ExceptionContext.ExceptionHandled;"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnActionExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnActionExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnActionExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnActionExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnActionExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnActionExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeControllerActionMethod:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterControllerActionMethod:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResultExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResultExecution:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResultExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResultExecuting:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeOnResultExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterOnResultExecuted:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeActionResult:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterActionResult:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.Razor.AfterViewPage:-"
                 + "Page"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.Razor.BeforeViewPage:-"
                 + "Page"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeViewComponent:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterViewComponent:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.ViewComponentBeforeViewExecute:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.ViewComponentAfterViewExecute:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.BeforeView:-"
                 + "View"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.AfterView:-"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.ViewFound:-"
                 + "ViewName"
                 "Microsoft.AspNetCore/Microsoft.AspNetCore.Mvc.ViewNotFound:-"
                 + "ViewName" |]
          ExtensionLoggingFilterSpecs = Array.empty<string> }


let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { Broadcast = broadcast
                ProcessName = proc.ProcessName
                ActionsCache = DataCache<_, _>(20) }
              :> obj
      Subscribe = subscribe }
