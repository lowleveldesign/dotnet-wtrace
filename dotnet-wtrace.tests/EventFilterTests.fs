module WTrace.Dotnet.Tests.EventFilterTests

open System
open NUnit.Framework
open FsUnit
open WTrace.Dotnet
open WTrace.Dotnet.Tracing
open System.Diagnostics.Tracing


[<Test>]
let TestRealtimeFilters () =
    let now = DateTime.Now

    let ev = {
        TraceEvent.EventId = 1
        TimeStamp = now
        ActivityId = ""
        ProcessId = 1
        ProcessName = "test"
        ThreadId = 1
        EventName = "TestTask/TestOpcode"
        EventLevel = EventLevel.Critical
        Path = "non-existing-path"
        Details = "short details"
        Result = 1
    }

    let filterFunction =
        [| EventName ("~", "TestOpcode") |]
        |> EventFilter.buildFilterFunction
    ev |> filterFunction |> should be True

    let filterFunction =
        [| EventName ("=", "TestTask/TestOpcode") |]
        |> EventFilter.buildFilterFunction
    ev |> filterFunction |> should be True

    let filterFunction =
        [| EventName ("=", "TestTask") |]
        |> EventFilter.buildFilterFunction
    ev |> filterFunction |> should be False

    let filterFunction = [| |] |> EventFilter.buildFilterFunction
    ev |> filterFunction |> should be True

[<Test>]
let TestFilterParsing () =
    match EventFilter.parseFilter "name= Test" with
    | EventName (op, v) ->
        op |> should equal "="
        v |> should equal "Test"
    | _ -> Assert.Fail()

    match EventFilter.parseFilter "DETAILs    ~  test message   " with
    | Details (op, v) ->
        op |> should equal "~"
        v |> should equal "test message"
    | _ -> Assert.Fail()

    match EventFilter.parseFilter "  test event   " with
    | EventName (op, v) ->
        op |> should equal "~"
        v |> should equal "test event"
    | _ -> Assert.Fail()

