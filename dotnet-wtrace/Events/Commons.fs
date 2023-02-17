namespace WTrace.Dotnet.Events

open System
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.NETCore.Client
open System.Collections.Generic
open System.Diagnostics.Tracing
open WTrace.Dotnet
open System.Diagnostics
open Microsoft.Diagnostics.Tracing.Parsers.Clr

type EtwEvent = Microsoft.Diagnostics.Tracing.TraceEvent

type NtKeywords = Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords

type IdGenerator = unit -> int32

type EventBroadcast = TraceEvent -> unit

type ProcessInfo =
    { ProcessName: string
      ProcessId: int32 }

// Mutable class for storing the session state
type WTraceEventSource(eventSource: TraceEventSource, level: EventLevel) =
    let clrRundown = ClrRundownTraceEventParser(eventSource)

    member _.Clr = eventSource.Clr

    member _.ClrRundown = clrRundown

    member _.Dynamic = eventSource.Dynamic

    member _.EventLevel = level


type ProviderSpecification =
    { DiagnosticSourceFilterAndPayloadSpecs: array<string>
      ExtensionLoggingFilterSpecs: array<string>
      Providers: array<EventPipeProvider> }

type EventPipeEventHandler =
    { GetProviderSpec: EventLevel -> ProviderSpecification (* providers *)

      Initialize: ProcessInfo * EventBroadcast (* broadcast API *)  -> obj (* handler state *)

      Subscribe: WTraceEventSource (* ETW trace session state *)  * IdGenerator (* generates unique ids for events *)  * obj (* handler state *)  -> unit }

module internal HandlerCommons =

    type Microsoft.Diagnostics.Tracing.TraceEvent with
        member this.PayloadCast<'T>(name: string) = this.PayloadByName(name) :?> 'T

    [<Literal>]
    let DiagnosticSourceEventSourceName = "Microsoft-Diagnostics-DiagnosticSource"

    [<Literal>]
    let LoggingEventSourceName = "Microsoft-Extensions-Logging"

    let getActivityId (ev: EtwEvent) =
        if ev.ActivityID = Guid.Empty then
            ""
        else
            StartStopActivityComputer.ActivityPathString(ev.ActivityID)

    let t2e (lvl: TraceEventLevel) = enum<EventLevel> (int32 lvl)

    let handleEvent<'T, 'S when 'T :> EtwEvent>
        (lvl: EventLevel)
        (idgen: IdGenerator)
        (state: 'S)
        handler
        (ev: 'T)
        : unit =
        if int32 ev.Level <= int32 lvl then
            handler (idgen ()) state ev

    let handleEventNoId<'T, 'S when 'T :> EtwEvent> (lvl: EventLevel) (state: 'S) handler (ev: 'T) : unit =
        if int32 ev.Level >= int32 lvl then
            handler state ev

    let printSize (n: int64) =
        if n > 1_000_000_000L then
            sprintf "%.00fGB" ((float n) / 1_000_000_000.0)
        elif n > 1_000_000L then
            sprintf "%.00fMB" ((float n) / 1_000_000.0)
        elif n > 1_000L then
            sprintf "%.00fKB" ((float n) / 1_000.0)
        else
            sprintf "%dB" n

    let tryFindindMatchingActivity activityKeywords (eventName : string) activityId =
        let dotind = eventName.LastIndexOf('.')

        if activityId <> "" && dotind >= 0 then
            let chooser (before: string, after: string) =
                let shortenEventName = eventName.AsSpan(dotind + 1)

                if shortenEventName.StartsWith(before.AsSpan(), StringComparison.Ordinal) then
                    let afterEventName =
                        String.Concat(after.AsSpan(), shortenEventName.Slice(before.Length))

                    Some(afterEventName, true)
                elif shortenEventName.StartsWith(after.AsSpan(), StringComparison.Ordinal) then
                    Some(String(shortenEventName), false)
                else
                    None

            activityKeywords
            |> Seq.choose chooser
            |> Seq.tryHead
        else None


type internal DataCache<'K, 'V when 'K: equality>(capacity: int32) =

    let buffer =
        Array.create<'K> capacity Unchecked.defaultof<'K>

    let mutable currentIndex = 0
    let cache = Dictionary<'K, 'V>(capacity)

    member _.Add(k, v) =
        currentIndex <- (currentIndex + 1) % capacity
        let previousKey = buffer.[currentIndex]

        if previousKey <> Unchecked.defaultof<'K> then
            cache.Remove(previousKey) |> ignore

        cache.[k] <- v
        buffer.[currentIndex] <- k
        Debug.Assert(cache.Count <= capacity, "[Cache] cache.Count < capacity")

    member _.Remove = cache.Remove

    member _.ContainsKey = cache.ContainsKey

    member _.TryGetValue = cache.TryGetValue

    member this.Item
        with get k = cache.[k]
        and set k v =
            if cache.ContainsKey(k) then
                cache.[k] <- v
            else
                this.Add(k, v)


type internal DataCacheWithCount<'K, 'V when 'K: equality>(capacity: int32) =

    let cache = DataCache<'K, 'V>(capacity)
    let counts = Dictionary<'K, int32>(capacity)

    member _.Add(k, v) =
        cache.Add(k, v)
        counts.Add(k, 1)

    member _.Remove k =
        match counts.TryGetValue(k) with
        | true, cnt when cnt = 1 ->
            cache.Remove(k) |> ignore
            counts.Remove(k) |> ignore
        | true, cnt -> counts.[k] <- cnt - 1
        | false, _ -> ()

    member _.ContainsKey = cache.ContainsKey

    member _.TryGetValue = cache.TryGetValue

    member this.Item
        with get k = cache.[k]
        and set k v =
            match counts.TryGetValue(k) with
            | true, cnt ->
                cache.[k] <- v
                counts.[k] <- cnt + 1
            | false, _ -> this.Add(k, v)
