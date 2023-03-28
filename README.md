# dotnet-wtrace

![.NET](https://github.com/lowleveldesign/dotnet-wtrace/workflows/build/badge.svg)

**Table of contents**:
<!-- MarkdownTOC -->

- [Introduction](#introduction)
- [Installation](#installation)
- [Tracing targets](#tracing-targets)
- [Filtering events](#filtering-events)
- [Event handlers](#event-handlers)
- [Understanding dotnet-wtrace output](#understanding-dotnet-wtrace-output)

<!-- /MarkdownTOC -->

## Introduction

Dotnet-wtrace is a command-line tool for reading **trace events emitted by .NET Core applications**. It can **trace a process from its launch, attach to an already running process, or read a .nettrace file**. Compared to dotnet-trace, it outputs events as they arrive (there is some delay because of the buffering on the target process side). It is not meant to replace dotnet-trace but rather help you in the initial problem diagnosis. To make the events data more readable, dotnet-wtrace does some preprocessing and might not show all the event fields. I implemented several handlers for the most useful (in my opinion) events. Those events include exceptions, GC, loader, ASP.NET Core, EF Core, and network events. You may limit the output by selecting specific handlers and setting filters.

As dotnet-wtrace relies on Event Pipes, it will work with .NET Core applications starting with version 2.1. The option to trace the launch of a process is available for applications targeting .NET Core 3.1 and newer.

An example command line:

```
dotnet-wtrace --handlers network,gc,loader /tmp/requestapp https://example.net
```

The available options are listed below:

```
Usage: dotnet-wtrace [OPTIONS] [pid|imagename args|.nettrace file]

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
```

## Installation

The precompiled binaries are available on the [release page](https://github.com/lowleveldesign/dotnet-wtrace/releases).

You may also install dotnet-wtrace as one of the dotnet global tools:

```
dotnet tool install -g dotnet-wtrace
```

## Tracing targets

Dotnet-wtrace identifies the tracing target from the first free argument in its command line. If it is a numeric value, dotnet-wtrace assumes it represents an ID of a running process and will try to attach to it. If it is a path that ends with the .nettrace extension, dotnet-wtrace tries to read events from this file. In all other cases, it will try to launch a process from a given path, passing arguments that follow the executable path to the target process.

```
# Start a new testapp process with some arguments
dotnet-wtrace testapp -a op1 -b op2 arg1
# Attach to a process with ID 1234
dotnet-wtrace 1234
# Load events from a .nettrace file
dotnet-wtrace /tmp/testapp.nettrace
```

## Filtering events

We may define an event filter with the **-f/-filter** option. The filter is built from a **keyword**, an **operator**, and a **value**. The keyword represents an event field and must be one of the following values:

- **name** - the event name
- **path** - the event path
- **details** - the event details

The **operators** are the same for numeric and text values and include: =, <>, <=, >=, ~. For numbers, the ~ operator has the same effect as the = operator. For text fields, the >= operator returns true if the field value starts with a given text value. Consequently, the <= operator returns true if the field value ends with a given text value. The ~ operator returns true if the field value contains a given text value. The text filters are case-insensitive.

The **value** part of the filter string is everything that comes after the operator sign, except for white spaces at the beginning and the end of the text value. Therefore, you don't need to use any apostrophes inside the filter text unless you want them to be a part of the text value.

You may define **multiple filters** for a trace session. Dotnet-wtrace combines them similarly to Process Monitor, so **filters with the same keyword are OR-ed together** (disjunction). **Filters which keywords differ are AND-ed together** (conjunction). At the start, dotnet-wtrace will print the parsed filters so you can verify if it's what you expected.

```
# Trace a process with PID 1234 and show only GC/Start and GC/End events
dotnet-wtrace -f “name = GC/Start” -f “name = GC/End” 1234
```

## Event handlers

Apart from defining filters, we may also specify which handlers dotnet-wtrace should enable in the session. Handlers are the components responsible for collecting and parsing trace events. Each handler handles a unique set of events. If we disable a handler, none of its events will appear in the live trace output. The following handlers are available:

- **network** - for collecting network events (TCP, UDP, DNS, TLS, HTTP)
- **loader** - for collecting assembly loader events
- **gc** - for collecting GC events
- **aspnet** - for collecting ASP.NET Core events
- **efcore** - for collecting Entity Framework Core (only SQL commands)

There is also a **default handler** which collects critical and important events, such as exceptions. It is always enabled.

If you don't use the --handlers option, dotnet-wtrace will enable GC, loader, and network handlers.

```
# Trace only GC and default events
dotnet-wtrace --handlers gc
```

## Understanding dotnet-wtrace output

Example output from dotnet-wtrace may looks as follows:

![screenshot](screenshot.png)

Let's go briefly through the data we see there. The first column shows the **timestamp** of an event. Then we have a **process** name with the **process ID** and the **current thread ID** in parentheses, for example, `requestapp (16662.16677)` (PID is 16662, TID equals 16677). The next column includes the **event name**, for example, `Loader/KnownPathProbed`. The event name is composed of the category name and the task name. The category name impacts the output coloring - events belonging to the same category have the same color, different from events originating from other categories (if you don't like the coloring, you may disable it with the --nocolor switch). After the event name, we can see the **event severity level** - if you leave coloring enabled, error and critical levels will be red. The next column is the **activity ID**. Dotnet-wtrace configures `System.Threading.Tasks.TplEventSource` to track the event activities. Activities help identify related events, for example, events belonging to a given HTTP request. And they can be nested. GC events have activity IDs set to the current GC number. The text after the activity ID column is not strictly defined and differs between event types.
