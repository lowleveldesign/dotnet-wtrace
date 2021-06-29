namespace WTrace.Dotnet.Tracing

open System
open System.Diagnostics.Tracing
open WTrace.Dotnet

type TraceTarget =
    | NewProcess of list<string>
    | RunningProcess of int32
    | TraceFile of string

module EventLevel =

    let parse (v: string) =
        match Int32.TryParse(v) with
        | (true, v) when v >= 1 && v <= 5 -> Ok(enum<EventLevel> (v))
        | (true, v) -> Error $"Invalid level value: %d{v}. Should be between 1 and 5."
        | (false, _) when v === "debug" || v === "verbose" -> Ok EventLevel.Verbose
        | (false, _) when v === "info" -> Ok EventLevel.Informational
        | (false, _) when v === "warning" -> Ok EventLevel.Warning
        | (false, _) when v === "error" -> Ok EventLevel.Error
        | (false, _) when v === "critical" -> Ok EventLevel.Critical
        | (false, _) -> Error $"Invalid level value: %s{v}"

    let toString lvl =
        match lvl with
        | EventLevel.Critical -> "critical"
        | EventLevel.Error -> "error"
        | EventLevel.Warning -> "warning"
        | EventLevel.LogAlways
        | EventLevel.Informational -> "info"
        | EventLevel.Verbose -> "verbose"
        | v -> "[undefined]"
