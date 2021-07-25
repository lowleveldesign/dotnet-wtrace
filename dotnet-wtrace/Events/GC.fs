module WTrace.Dotnet.Events.GC

open System
open System.Collections.Generic
open System.Diagnostics.Tracing
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Tracing.Parsers.Clr

(*
Based on:
- https://medium.com/criteo-engineering/spying-on-net-garbage-collector-with-net-core-eventpipes-9f2a986d5705
- https://github.com/Maoni0/mem-doc/blob/master/doc/.NETMemoryPerformanceAnalysis.md#gc-event-sequence
*)

type private GCDetails =
    { StartElapsedMSec: float
      TotalPauseDurationMSec: float
      EphemeralPauseDurationMSec: float // in BGC, this is the pauses of the ephmeral GCs
      Number: int32
      Reason: GCReason
      Type: GCType
      Generations: string
      Completed: bool }

type private RuntimeSuspendDetails =
    { StartElapsedMSec: float
      SuspendReason: GCSuspendEEReason }

type private GCHandlerState =
    { ProcessName: string
      Broadcast: EventBroadcast
      RuntimeSuspends: Stack<RuntimeSuspendDetails>
      RunningGCs: Stack<GCDetails>
      Finalizations: Stack<float> }

[<AutoOpen>]
module private H =

    let logger = Logger.Tracing

    let gens = [| "Gen0"; "Gen1"; "Gen2" |]

    let handleGCTriggered id state (ev: GCTriggeredTraceData) =
        Debug.Assert(state.RunningGCs.Count <= 1, "there should be 0 or 1 GC running")

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = getActivityId ev
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/Triggered"
              EventLevel = t2e ev.Level
              Path = ""
              Details = $"reason: %A{ev.Reason}"
              Result = 0 }

        state.Broadcast ev

    let handleGCStart id state (ev: GCStartTraceData) =
        Debug.Assert(state.RunningGCs.Count < 2)

        if state.RunningGCs.Count > 0
           && state.RunningGCs.Peek().Completed then
            state.RunningGCs.Pop() |> ignore // remove completed BGC

        let generations =
            gens
            |> Seq.take (ev.Depth + 1)
            |> String.concat ", "

        state.RunningGCs.Push(
            { StartElapsedMSec = ev.TimeStampRelativeMSec
              TotalPauseDurationMSec = 0.0
              EphemeralPauseDurationMSec = 0.0
              Number = ev.Count
              Reason = ev.Reason
              Type = ev.Type
              Generations = generations
              Completed = false }
        )

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = $"GC #%d{ev.Count}"
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/Start"
              EventLevel = t2e ev.Level
              Path = generations
              Details = $"reason: %O{ev.Reason}, type: %O{ev.Type}"
              Result = 0 }

        state.Broadcast ev

    let handleGCStop id state (ev: GCEndTraceData) =
        match state.RunningGCs.TryPop() with
        | (true, runningGC) ->
            Debug.Assert(runningGC.Number = ev.Count)

            let duration =
                ev.TimeStampRelativeMSec
                - runningGC.StartElapsedMSec

            state.RunningGCs.Push({ runningGC with Completed = true })

            // we report the pause time only for the BGC
            let details =
                if runningGC.Type = GCType.BackgroundGC then
                    sprintf
                        "duration: %.3fms, pause: %.3fms (ephemeral: %.3f)"
                        duration
                        runningGC.TotalPauseDurationMSec
                        runningGC.EphemeralPauseDurationMSec
                else
                    $"duration: %.3f{duration}ms"

            let ev =
                { EventId = id
                  TimeStamp = ev.TimeStamp
                  ActivityId = $"GC #%d{ev.Count}"
                  ProcessId = ev.ProcessID
                  ProcessName = state.ProcessName
                  ThreadId = ev.ThreadID
                  EventName = "GC/End"
                  EventLevel = t2e ev.Level
                  Path = runningGC.Generations
                  Details = details
                  Result = 0 }

            state.Broadcast ev
        | (false, _) ->
            logger.TraceWarning($"[GC Handler] Could not find a running GC in the trace (GC/End, GC #{ev.Count})")

    let handleGCSuspendEEStart id state (ev: GCSuspendEETraceData) =
        Debug.Assert(state.RuntimeSuspends.Count < 4)

        state.RuntimeSuspends.Push(
            { StartElapsedMSec = ev.TimeStampRelativeMSec
              SuspendReason = ev.Reason }
        )

        if ev.Reason = GCSuspendEEReason.SuspendForGCPrep
           || ev.Reason = GCSuspendEEReason.SuspendForGC then
            let activity, path =
                match state.RunningGCs.TryPeek() with
                | (true, gc) when gc.Completed ->
                    state.RunningGCs.Pop() |> ignore
                    "", ""
                | (true, gc) -> $"GC #%d{gc.Number}", gc.Generations
                | (false, _) -> "", ""

            let ev =
                { EventId = id
                  TimeStamp = ev.TimeStamp
                  ActivityId = activity
                  ProcessId = ev.ProcessID
                  ProcessName = state.ProcessName
                  ThreadId = ev.ThreadID
                  EventName = "GC/RuntimeSuspended"
                  EventLevel = t2e ev.Level
                  Path = path
                  Details = $"reason: %A{ev.Reason}"
                  Result = 0 }

            state.Broadcast ev

    let handleGCRestartEEStop id state (ev: GCNoUserDataTraceData) =
        match state.RuntimeSuspends.TryPop() with
        | (true, suspension) ->
            if suspension.SuspendReason = GCSuspendEEReason.SuspendForGCPrep
               || suspension.SuspendReason = GCSuspendEEReason.SuspendForGC then
                let duration =
                    ev.TimeStampRelativeMSec
                    - suspension.StartElapsedMSec

                let activity, path =
                    match state.RunningGCs.TryPop() with
                    | (true, gc) when not gc.Completed ->
                        // This usually happens only on BGC, but I observed it also for NonConcurrent WKS GC.
                        // I'm not sure if it's a problem with the order of events or yet something else.
                        Debug.Assert(state.RunningGCs.Count = 0, "if it's BGC, there should be no other GC")

                        state.RunningGCs.Push(
                            { gc with
                                  TotalPauseDurationMSec = gc.TotalPauseDurationMSec + duration }
                        )

                        $"GC #%d{gc.Number}", gc.Generations
                    | (true, gc) when state.RunningGCs.Count > 0 ->
                        Debug.Assert(state.RunningGCs.Count = 1, "there should be only on BGC running")
                        let bgc = state.RunningGCs.Pop()

                        state.RunningGCs.Push(
                            { bgc with
                                  TotalPauseDurationMSec = bgc.TotalPauseDurationMSec + duration
                                  EphemeralPauseDurationMSec = bgc.EphemeralPauseDurationMSec + duration }
                        )

                        $"GC #%d{gc.Number}", gc.Generations
                    | (true, gc) -> $"GC #%d{gc.Number}", gc.Generations
                    | (false, _) -> "", ""

                let ev =
                    { EventId = id
                      TimeStamp = ev.TimeStamp
                      ActivityId = activity
                      ProcessId = ev.ProcessID
                      ProcessName = state.ProcessName
                      ThreadId = ev.ThreadID
                      EventName = "GC/RuntimeResumed"
                      EventLevel = t2e ev.Level
                      Path = path
                      Details = $"suspension time: %.3f{duration}ms"
                      Result = 0 }

                state.Broadcast ev
            | (false, _) ->
                logger.TraceWarning("[GC Handler] Could not find a matching GC/RuntimeSuspended event (GC/RuntimeResumed).")

    let handleGCGlobalHeapHistory id state (ev: GCGlobalHeapHistoryTraceData) =
        Debug.Assert(state.RunningGCs.Count > 0, "there should be a GC registered")
        let gc = state.RunningGCs.Peek()

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = $"GC #%d{gc.Number}"
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/AdditionalInfo"
              EventLevel = t2e ev.Level
              Path = gc.Generations
              Details =
                  sprintf
                      "condemned: Gen%d, compacting: %b"
                      ev.CondemnedGeneration
                      (ev.GlobalMechanisms
                       &&& GCGlobalMechanisms.Compaction = GCGlobalMechanisms.Compaction)
              Result = 0 }

        state.Broadcast ev

    let handleGCHeapStatsTotals id state (ev: GCHeapStatsTraceData) =
        Debug.Assert(state.RunningGCs.Count > 0, "there should be a GC registered")
        let gc = state.RunningGCs.Peek()

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = $"GC #%d{gc.Number}"
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/HeapTotals"
              EventLevel = t2e ev.Level
              Path = gc.Generations
              Details =
                  sprintf
                      "total: %s, gen0: %s, gen1: %s, gen2: %s, LOH: %s, POH: %s, pinned no: %d, GC handles no: %d"
                      (printSize ev.TotalHeapSize)
                      (printSize ev.GenerationSize0)
                      (printSize ev.GenerationSize1)
                      (printSize ev.GenerationSize2)
                      (printSize ev.GenerationSize3)
                      (printSize ev.GenerationSize4)
                      ev.PinnedObjectCount
                      ev.GCHandleCount
              Result = 0 }

        state.Broadcast ev

    let handleGCHeapStatsPromoted id state (ev: GCHeapStatsTraceData) =
        Debug.Assert(state.RunningGCs.Count > 0, "there should be a GC registered")
        let gc = state.RunningGCs.Peek()

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = $"GC #%d{gc.Number}"
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/HeapPromoted"
              EventLevel = t2e ev.Level
              Path = gc.Generations
              Details =
                  sprintf
                      "total: %s, gen0: %s, gen1: %s, gen2: %s, LOH: %s, POH: %s"
                      (printSize ev.TotalPromoted)
                      (printSize ev.TotalPromotedSize0)
                      (printSize ev.TotalPromotedSize1)
                      (printSize ev.TotalPromotedSize2)
                      (printSize ev.TotalPromotedSize3)
                      (printSize ev.TotalPromotedSize4)
              Result = 0 }

        state.Broadcast ev

    let handleGCFinalizersStart id state (ev: GCNoUserDataTraceData) =
        Debug.Assert(state.Finalizations.Count < 1)
        state.Finalizations.Push(ev.TimeStampRelativeMSec)

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = ""
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/FinalizationStart"
              EventLevel = t2e ev.Level
              Path = ""
              Details = ""
              Result = 0 }

        state.Broadcast ev

    let handleGCFinalizersStop id state (ev: GCFinalizersEndTraceData) =
        Debug.Assert(state.Finalizations.Count = 1)

        let startTime =
            if state.Finalizations.Count > 0 then
                state.Finalizations.Pop()
            else
                ev.TimeStampRelativeMSec

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = ""
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = "GC/FinalizationEnd"
              EventLevel = t2e ev.Level
              Path = ""
              Details = $"duration: %.3f{ev.TimeStampRelativeMSec - startTime}ms"
              Result = 0 }

        state.Broadcast ev

    let getProviderSpec _ =
        { Providers = [| EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x1L) |]
          DiagnosticSourceFilterAndPayloadSpecs = Array.empty<string>
          ExtensionLoggingFilterSpecs = Array.empty<string> }

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> GCHandlerState
        let handleEvent h = Action<_>(handleEvent EventLevel.Informational idgen state h)

        session.Clr.add_GCTriggered (handleEvent handleGCTriggered)
        session.Clr.add_GCStart (handleEvent handleGCStart)
        session.Clr.add_GCStop (handleEvent handleGCStop)
        session.Clr.add_GCSuspendEEStart (handleEvent handleGCSuspendEEStart)
        session.Clr.add_GCRestartEEStop (handleEvent handleGCRestartEEStop)
        session.Clr.add_GCFinalizersStart (handleEvent handleGCFinalizersStart)
        session.Clr.add_GCFinalizersStop (handleEvent handleGCFinalizersStop)
        session.Clr.add_GCHeapStats (handleEvent handleGCHeapStatsPromoted)
        session.Clr.add_GCHeapStats (handleEvent handleGCHeapStatsTotals)
        session.Clr.add_GCGlobalHeapHistory (handleEvent handleGCGlobalHeapHistory)

let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { ProcessName = proc.ProcessName
                Broadcast = broadcast
                RuntimeSuspends = Stack<RuntimeSuspendDetails>(4)
                RunningGCs = Stack<GCDetails>(2)
                Finalizations = Stack<float>(1) }
              :> obj
      Subscribe = subscribe }
