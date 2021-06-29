namespace WTrace.Dotnet

open System
open System.Diagnostics.Tracing

type TraceEvent = {
    EventId : int32
    TimeStamp : DateTime
    ProcessId : int32
    ProcessName : string
    ThreadId : int32
    ActivityId : string
    EventName : string
    EventLevel : EventLevel
    Path : string
    Details : string
    Result : int32
}
