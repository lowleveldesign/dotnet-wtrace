module WTrace.Dotnet.Program

open System
open System.Diagnostics
open System.Diagnostics.Tracing
open System.IO
open System.Reflection
open System.Threading
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Tracing

let appAssembly = Assembly.GetEntryAssembly()
let appName = appAssembly.GetName()

let showCopyright () =
    printfn ""
    printfn "%s v%s - collects .NET trace events" appName.Name (appName.Version.ToString())

    let customAttrs =
        appAssembly.GetCustomAttributes(typeof<AssemblyCompanyAttribute>, true)

    assert (customAttrs.Length > 0)

    printfn
        "Copyright (C) %d %s"
        2021
        (customAttrs.[0] :?> AssemblyCompanyAttribute)
            .Company

    printfn "Visit https://wtrace.net to learn more"
    printfn ""

let showHelp () =
    printfn "Usage: %s [OPTIONS] [pid|imagename args|.nettrace file]" appName.Name

    printfn
        @"
Options:
  -f, --filter=FILTER   Displays only events which satisfy a given FILTER.
                        (Does not impact the summary)
  --handlers=HANDLERS   Displays only events coming from the specified HANDLERS.
  --newconsole          Starts the process in a new console window. (Windows only)
  --nocolor             Do not color the output.
  -l, --level=LEVEL     Log level (1 [critical] - 5 [debug]), default: 4 [info]
  -v, --verbose         Shows wtrace-dotnet diagnostics logs.
  -h, --help            Shows this message and exits.

  The HANDLERS parameter is a list of handler names, separated with a comma.

  Accepted handlers include:
    network   - to receive network events
    loader    - to receive assembly loader events
    gc        - to receive GC events
    aspnet    - to receive ASP.NET Core events
    efcore    - to receive EF Core events

  The default handler is always enabled and displays information about exceptions
  and runtime.

  Example: --handlers 'loader,gc'

  Each FILTER is built from a keyword, an operator, and a value. You may
  define multiple events (filters with the same keywords are OR-ed).

  Keywords include:
    name    - filtering on the event name
    path    - filtering on the event path
    details - filtering on the event details

  Operators include:
    = (equals), <> (does not equal), <= (ends with), >= (starts with), ~ (contains)

  Examples:
    -f 'name >= Sockets/'
    -f 'level <= 4'
    -f 'name = GC/Start' -f 'name = GC/End'
"

let isFlagEnabled args flags =
    flags
    |> Seq.exists (fun f -> args |> Map.containsKey f)

let parseHandlers args =
    let createHandlers (handlers: string) =
        let createHandler (name: string) =
            if name === "network" then
                Network.createEventHandler ()
            elif name === "loader" then
                Loader.createEventHandler ()
            elif name === "gc" then
                GC.createEventHandler ()
            elif name === "aspnet" then
                AspNetCore.createEventHandler ()
            elif name === "efcore" then
                EFCore.createEventHandler ()
            elif name === "default" then
                Default.createEventHandler ()
            else
                failwith (sprintf "Invalid handler name: '%s'" name)

        try
            let handlerNames =
                handlers.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun name -> name.Trim().ToLower())

            let handlerNames =
                if handlerNames |> Array.contains "default" then
                    handlerNames
                else
                    handlerNames |> Array.append [| "default" |]

            printfn "HANDLERS"
            printfn "  %s" (handlerNames |> String.concat ", ")
            printfn ""

            Ok(handlerNames |> Array.map createHandler)
        with Failure msg -> Error msg

    match args |> Map.tryFind "handlers" with
    | None -> createHandlers "default,gc,loader,network" // default set of handlers
    | Some [ handlers ] -> createHandlers handlers
    | _ -> Error("Handlers can be specified only once.")

let parseFilters args =
    let p filters =
        try
            Ok(filters |> List.map EventFilter.parseFilter)
        with EventFilter.ParseError msg -> Error msg

    match args |> Map.tryFind "f" with
    | None ->
        match args |> Map.tryFind "filters" with
        | None -> Ok List.empty<EventFilter>
        | Some filters -> p filters
    | Some filters -> p filters


let parseLevel args =
    match args |> Map.tryFind "l" with
    | None ->
        match args |> Map.tryFind "level" with
        | None -> Ok EventLevel.Informational // info
        | Some (lvl :: _) -> EventLevel.parse lvl
        | _ ->
            Debug.Assert(false, "-l option parsed as flag")
            failwith "unacceptable level value"
    | Some (lvl :: _) -> EventLevel.parse lvl
    | _ ->
        Debug.Assert(false, "-l option parsed as flag")
        failwith "unacceptable level value"

let parseTarget args =
    let isInteger (v: string) =
        let r, _ = Int32.TryParse(v)
        r

    match args |> Map.tryFind "" with
    | Some [ pid ] when isInteger pid -> Ok(RunningProcess(Int32.Parse(pid)))
    | Some [ traceFile ] when Path.GetExtension(traceFile) = ".nettrace" -> Ok(TraceFile(traceFile))
    | Some procArgs -> Ok(NewProcess procArgs)
    | None -> Error "Missing target process specification (PID or path)."

let start (args: Map<string, list<string>>) =
    result {
        let isFlagEnabled = isFlagEnabled args

        if [| "v"; "verbose" |] |> isFlagEnabled then
            Trace.AutoFlush <- true
            Logger.initialize (SourceLevels.Verbose, [ new TextWriterTraceListener(Console.Out) ])

        let! filters = parseFilters args
        printfn "FILTERS"

        if filters |> List.isEmpty then
            printfn "  [none]"
        else
            EventFilter.printFilters filters

        printfn ""

        let! level = parseLevel args
        printfn "LEVEL"
        printfn $"  %d{int32 level} [%s{EventLevel.toString level}]"
        printfn ""

        let! handlers = parseHandlers args

        use cts = new CancellationTokenSource()

        Console.CancelKeyPress.Add
            (fun ev ->
                ev.Cancel <- true
                printfn "Closing the trace session. Please wait..."
                cts.Cancel())

        let! traceTarget = parseTarget args
        let newConsole = ([| "newconsole" |] |> isFlagEnabled)
        let noColor = ([| "nocolor" |] |> isFlagEnabled)

        do! TraceControl.startTrace traceTarget handlers filters level newConsole noColor cts.Token
    }

[<EntryPoint>]
let main argv =
    let flags =
        [| "s"
           "system"
           "c"
           "children"
           "newconsole"
           "nocolor"
           "v"
           "verbose"
           "h"
           "?"
           "help" |]

    let args = argv |> CommandLine.parseArgs flags

    showCopyright ()

    if [| "h"; "help"; "?" |] |> isFlagEnabled args then
        showHelp ()
        0
    else
        match start args with
        | Ok _ -> 0
        | Error msg ->
            printfn "[ERROR] %s" msg
            1
