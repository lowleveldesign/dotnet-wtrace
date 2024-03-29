﻿namespace WTrace.Dotnet.Tracing

open System
open WTrace.Dotnet

type TraceEventLevel = System.Diagnostics.Tracing.EventLevel

type EventFilter =
| EventName of string * string
| Details of string * string
| Path of string * string

type EventFilterSettings = {
    Filters : array<EventFilter>
}

module EventFilter =

    [<AutoOpen>]
    module private H =
        let createCheck op =
            match op with
            | "=" | "~" ->( = )
            | "<>" -> ( <> )
            | ">=" -> ( >= )
            | "<=" -> ( <= )
            | _ ->
                Debug.Assert(false, sprintf "Invalid filter operator '%s' for filter" op)
                ( = )

        let createCheckString op =
            match op with
            | "=" -> (===)
            | "<>" -> fun a b -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase) <> 0
            | ">=" -> fun a b -> a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            | "<=" -> fun a b -> a.EndsWith(b, StringComparison.OrdinalIgnoreCase)
            | "~" -> fun a b -> a.IndexOf(b, StringComparison.OrdinalIgnoreCase) <> -1
            | _ -> 
                Debug.Assert(false, sprintf "Invalid filter operator '%s' for filter" op)
                ( = )

        let buildFilterFunction filter =
            match filter with
            | EventName (op , s) ->
                let check = createCheckString op
                ("name", fun ev -> check ev.EventName s)
            | Path (op, s) ->
                let check = createCheckString op
                ("path", fun ev -> check ev.Path s)
            | Details (op, s) ->
                let check = createCheckString op
                ("details", fun ev -> check ev.Details s)

    let buildFilterFunction filters =
        let filterGroups =
            filters
            |> Seq.map buildFilterFunction
            |> Seq.groupBy (fun (category, _) -> category)
            |> Seq.map (fun (_, s) -> s |> Seq.map (fun (c, f) -> f) |> Seq.toArray)
            |> Seq.toArray

        fun ev ->
            filterGroups
            |> Array.forall (fun filterGroup -> filterGroup |> Array.exists (fun f -> f ev))


    exception ParseError of string

    let parseFilter (filterStr : string) =
        let operators = [| "<>"; ">="; "<="; "~"; "=" |]

        match filterStr.Split(operators, 2, StringSplitOptions.None) with
        | [| filterName; filterValue |] ->
            let operator =
                if filterStr.[filterName.Length] = '=' then "="
                elif filterStr.[filterName.Length] = '~' then "~"
                else filterStr.Substring(filterName.Length, 2)
            let mutable n = 0
            let filterName = filterName.Trim()
            let filterValue = filterValue.Trim()
            if filterName === "name" then
                EventName (operator, filterValue)
            elif filterName === "path" then
                Path (operator, filterValue)
            elif filterName === "details" then
                Details (operator, filterValue)
            else raise (ParseError (sprintf "Invalid filter: '%s'" filterName))

        | [| eventName |] -> EventName ("~", eventName.Trim())
        | _ -> raise (ParseError (sprintf "Invalid filter definition: '%s'" filterStr))


    let printFilters filters =
        let buildFilterDescription filter =
            match filter with
            | EventName (op , s) -> ("Event name", sprintf "%s '%s'" op s)
            | Path (op, s) -> ("Path", sprintf "%s '%s'" op s)
            | Details (op, s) -> ("Details", sprintf "%s '%s'" op s)

        let printFiltersGroup name defs =
            printfn "  %s" name
            printfn "    %s" (defs |> String.concat " OR ")

        filters
        |> Seq.map buildFilterDescription
        |> Seq.groupBy (fun (name, _) -> name)
        |> Seq.iter (fun (name, s) -> s |> Seq.map (fun (_, f) -> f)
                                        |> printFiltersGroup name)

