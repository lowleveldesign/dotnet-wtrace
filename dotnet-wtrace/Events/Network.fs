module WTrace.Dotnet.Events.Network

open System
open System.Net.Security
open System.Security.Authentication
open System.Collections.Generic
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.NETCore.Client
open WTrace.Dotnet
open WTrace.Dotnet.Events
open WTrace.Dotnet.Events.HandlerCommons

type private NetworkHandlerState =
    { Broadcast: EventBroadcast
      ProcessName: string
      ProviderNamesWithAliases: Map<string, string> }

type private CredentialUse =
    | SECPKG_CRED_INBOUND = 0x1
    | SECPKG_CRED_OUTBOUND = 0x2
    | SECPKG_CRED_BOTH = 0x3

[<Flags>]
type private ContextFlags =
    | Zero = 0x0
    | Delegate = 0x1
    | MutualAuth = 0x2
    | ReplayDetect = 0x4
    | SequenceDetect = 0x8
    | Confidentiality = 0x10
    | UseSessionKey = 0x20
    | InitUseSuppliedCreds = 0x80
    | AllocateMemory = 0x100
    | Connection = 0x800
    | InitExtendedError = 0x4000
    | AcceptExtendedError = 0x8000
    | AcceptStream = 0x10000
    | AcceptIntegrity = 0x20000
    | InitManualCredValidation = 0x80000
    | ProxyBindings = 0x4000000
    | AllowMissingBindings = 0x10000000
    | UnverifiedTargetName = 0x20000000

type private SecurityStatus =
    | OutOfMemory = 0x80090300
    | InvalidHandle = 0x80090301
    | Unsupported = 0x80090302
    | TargetUnknown = 0x80090303
    | InternalError = 0x80090304
    | PackageNotFound = 0x80090305
    | NotOwner = 0x80090306
    | CannotInstall = 0x80090307
    | InvalidToken = 0x80090308
    | CannotPack = 0x80090309
    | QopNotSupported = 0x8009030a
    | NoImpersonation = 0x8009030b
    | LogonDenied = 0x8009030c
    | UnknownCredentials = 0x8009030d
    | NoCredentials = 0x8009030e
    | MessageAltered = 0x8009030f
    | OutOfSequence = 0x80090310
    | NoAuthenticatingAuthority = 0x80090311
    | IncompleteMessage = 0x80090318
    | IncompleteCredentials = 0x80090320
    | BufferNotEnough = 0x80090321
    | WrongPrincipal = 0x80090322
    | TimeSkew = 0x80090324
    | UntrustedRoot = 0x80090325
    | IllegalMessage = 0x80090326
    | CertUnknown = 0x80090327
    | CertExpired = 0x80090328
    | AlgorithmMismatch = 0x80090331
    | SecurityQosFailed = 0x80090332
    | SmartcardLogonRequired = 0x8009033e
    | UnsupportedPreauth = 0x80090343
    | BadBinding = 0x80090346
    | DowngradeDetected = 0x80090350
    | ApplicationProtocolMismatch = 0x80090367
    | OK = 0x0
    | ContinueNeeded = 0x90312
    | CompleteNeeded = 0x90313
    | CompAndContinue = 0x90314
    | ContextExpired = 0x90317
    | CredentialsNeeded = 0x90320
    | Renegotiate = 0x90321

[<AutoOpen>]
module private H =

    [<AutoOpen>]
    module private Fields =
        let Message = 0
        let Context = Message + 1
        let Member = Context + 1
        let HttpRequest = Member + 1
        let HttpRequest2 = HttpRequest + 1
        let HttpResponse = HttpRequest2 + 1
        let HttpClient = HttpResponse + 1
        let Socket = HttpClient + 1
        let SecureChannel = Socket + 1

        let indexes = Array.zeroCreate (SecureChannel + 1)

    let handleDiagnosticSourceEventSourceEvent id state (ev: EtwEvent) =
        if ev.PayloadCast<string>("SourceName") = "HttpHandlerDiagnosticListener" then
            let details =
                ev.PayloadCast<array<IDictionary<string, obj>>>("Arguments")
                |> Array.map (fun d -> sprintf "%s: %s" (d.["Key"] :?> string) (d.["Value"] :?> string))
                |> String.concat ", "

            let ev =
                { EventId = id
                  TimeStamp = ev.TimeStamp
                  ActivityId = getActivityId ev
                  ProcessId = ev.ProcessID
                  ProcessName = state.ProcessName
                  ThreadId = ev.ThreadID
                  EventName = sprintf "Network/%s" (ev.PayloadCast<string>("EventName"))
                  EventLevel = t2e ev.Level
                  Path = ""
                  Details = details
                  Result = 0 }

            state.Broadcast ev

    // Events filtered by this method do not provide any meaningful output
    // and could be skipped
    let shouldSkipPayload =
        function
        | struct ("Security", "Info", "VerifyRemoteCertificate") -> true
        | _ -> false

    let handleNetworkEventSourceEvent id state (ev: EtwEvent) =
        indexes.[Message] <- ev.PayloadIndex("message")
        indexes.[Context] <- ev.PayloadIndex("thisOrContextObject")
        indexes.[Member] <- ev.PayloadIndex("memberName")
        indexes.[HttpRequest] <- ev.PayloadIndex("httpRequestHash")
        indexes.[HttpRequest2] <- ev.PayloadIndex("requestId")
        indexes.[HttpResponse] <- ev.PayloadIndex("httpResponseHash")
        indexes.[HttpClient] <- ev.PayloadIndex("httpClientHash")
        indexes.[Socket] <- ev.PayloadIndex("socketHash")
        indexes.[SecureChannel] <- ev.PayloadIndex("secureChannelHash")

        let activity = getActivityId ev

        let context =
            if indexes.[Context] >= 0 then
                ev.PayloadValue(indexes.[Context]) :?> string
            elif indexes.[HttpClient] >= 0 then
                $"HttpClient#{ev.PayloadValue(indexes.[HttpClient])}"
            elif indexes.[HttpRequest] >= 0 then
                $"HttpRequest#{ev.PayloadValue(indexes.[HttpRequest])}"
            elif indexes.[HttpRequest2] >= 0 then
                $"HttpRequest#{ev.PayloadValue(indexes.[HttpRequest2])}"
            elif indexes.[HttpResponse] >= 0 then
                $"HttpResponse#{ev.PayloadValue(indexes.[HttpResponse])}"
            elif indexes.[Socket] >= 0 then
                $"Socket#{ev.PayloadValue(indexes.[Socket])}"
            elif indexes.[SecureChannel] >= 0 then
                $"SecureChannel#{ev.PayloadValue(indexes.[SecureChannel])}"
            else
                ""

        let providerAlias =
            (state.ProviderNamesWithAliases
             |> Map.find ev.ProviderName)

        let memberName =
            if indexes.[Member] >= 0 then
                ev.PayloadValue(indexes.[Member]) :?> string
            else
                ""

        let eventName = ev.EventName

        let message =
            if shouldSkipPayload struct (providerAlias, eventName, memberName) then
                ""
            elif indexes.[Message] >= 0 then
                ev.PayloadValue(indexes.[Message]) :?> string
            else
                match eventName with
                | "Enter" -> sprintf "params: %s" (ev.PayloadCast<string>("parameters"))
                | "Exit" -> sprintf "result: %s" (ev.PayloadCast<string>("result"))
                | "Associate" ->
                    Debug.Assert(
                        (context.StartsWith(ev.PayloadCast<string>("first"), StringComparison.Ordinal)),
                        "memberName should be the same as first context"
                    )

                    ev.PayloadCast<string>("second")
                | "ClientSendCompleted" ->
                    sprintf
                        "<HttpRequest#%d -> HttpResponse#%d> response: %s"
                        (ev.PayloadCast<int32>("httpRequestMessageHash"))
                        (ev.PayloadCast<int32>("httpResponseMessageHash"))
                        (ev.PayloadCast<string>("responseString"))
                | "HeadersInvalidValue" ->
                    sprintf
                        "name: %s, value: '%s'"
                        (ev.PayloadCast<string>("name"))
                        (ev.PayloadCast<string>("rawValue"))
                | "SslStreamCtor" ->
                    sprintf
                        "%s <=> %s"
                        (ev.PayloadCast<string>("localId"))
                        (ev.PayloadCast<string>("remoteId"))
                | "SecureChannelCtor" ->
                    sprintf
                        "host: %s, client certs count: %d, encryption policy: %O"
                        (ev.PayloadCast<string>("hostname"))
                        (ev.PayloadCast<int32>("clientCertificatesCount"))
                        (enum<EncryptionPolicy> (ev.PayloadCast<int32>("encryptionPolicy")))
                | "SecurityCertsAfterFiltering" ->
                    sprintf "filtered certs count: %d" (ev.PayloadCast<int32>("filteredCertsCount"))
                | "EnumerateSecurityPackages" -> sprintf "package: %s" (ev.PayloadCast<string>("securityPackage"))
                | "SentFrame" -> sprintf "TLS frame: %s" (ev.PayloadCast<string>("tlsFrame"))
                | "RemoteCertificate" -> sprintf "remote cert: %s" (ev.PayloadCast<string>("remoteCertificate"))
                | "SspiSelectedCipherSuite" ->
                    sprintf
                        "process: %s, TLS version: %O, cipher: %O, hash: %O, key exchange: %O"
                        (ev.PayloadCast<string>("process"))
                        (enum<SslProtocols> (ev.PayloadCast<int32>("sslProtocol")))
                        (enum<CipherAlgorithmType> (ev.PayloadCast<int32>("cipherAlgorithm")))
                        (enum<HashAlgorithmType> (ev.PayloadCast<int32>("hashAlgorithm")))
                        (enum<ExchangeAlgorithmType> (ev.PayloadCast<int32>("keyExchangeAlgorithm")))
                | "SspiPackageNotFound" -> sprintf "package name: %s" (ev.PayloadCast<string>("packageName"))
                | "AttemptingRestartUsingCert"
                | "SelectedCert" -> sprintf "client cert: %s" (ev.PayloadCast<string>("clientCertificate"))
                | "AcceptSecurityContext" ->
                    sprintf
                        "creds: %s, context: %s, flags: %O"
                        (ev.PayloadCast<string>("credential"))
                        (ev.PayloadCast<string>("context"))
                        (enum<ContextFlags> (ev.PayloadCast<int32>("inFlags")))
                | "AcquireDefaultCredential" ->
                    sprintf
                        "package name: %s, intent: %O"
                        (ev.PayloadCast<string>("packageName"))
                        (enum<CredentialUse> (ev.PayloadCast<int32>("intent")))
                | "AcquireCredentialsHandle" ->
                    sprintf
                        "package name: %s, intent: %O, auth data: %s"
                        (ev.PayloadCast<string>("packageName"))
                        (enum<CredentialUse> (ev.PayloadCast<int32>("intent")))
                        (ev.PayloadCast<string>("authData"))
                | "LocatingPrivateKey" -> sprintf "cert: %s" (ev.PayloadCast<string>("x509Certificate"))
                | "FoundCertInStore" -> sprintf "store: %s" (ev.PayloadCast<string>("store"))
                | "InitializeSecurityContext" ->
                    sprintf
                        "creds: %s, context: %s, targetName: %s, flags: %O"
                        (ev.PayloadCast<string>("credential"))
                        (ev.PayloadCast<string>("context"))
                        (ev.PayloadCast<string>("targetName"))
                        (enum<ContextFlags> (ev.PayloadCast<int32>("inFlags")))
                | "OperationReturnedSomething" ->
                    sprintf
                        "operation: %s, error code: %O"
                        (ev.PayloadCast<string>("operation"))
                        (enum<SecurityStatus> (ev.PayloadCast<int32>("errorCode")))
                | _ -> ""

        let details =
            sprintf
                "%s%s%s"
                (if context = "" then
                     ""
                 else
                     context + " ")
                (if memberName = "" then
                     ""
                 else
                     $"(%s{memberName}) ")
                message

        let ev =
            { EventId = id
              TimeStamp = ev.TimeStamp
              ActivityId = activity
              ProcessId = ev.ProcessID
              ProcessName = state.ProcessName
              ThreadId = ev.ThreadID
              EventName = $"Network/%s{providerAlias}%s{eventName}"
              EventLevel = t2e ev.Level
              Path = ""
              Details = details
              Result = 0 }

        state.Broadcast ev

    let handleNetworkEvent id state (ev: EtwEvent) =
        if ev.ProviderName = DiagnosticSourceEventSourceName then
            handleDiagnosticSourceEventSourceEvent id state ev
        else
            handleNetworkEventSourceEvent id state ev

    let providerNamesWithAliases =
        [| ("Private.InternalDiagnostics.System.Net.Primitives", "Network")
           ("Private.InternalDiagnostics.System.Net.NetworkInformation", "Network")
           ("Private.InternalDiagnostics.System.Net.Sockets", "Socket")
           ("Private.InternalDiagnostics.System.Net.NameResolution", "DNS")
           ("Private.InternalDiagnostics.System.Net.Mail", "Mail")
           ("Private.InternalDiagnostics.System.Net.Requests", "Request")
           ("Private.InternalDiagnostics.System.Net.WinHttpHandler", "WinHttp")
           ("Private.InternalDiagnostics.System.Net.HttpListener", "HttpListener")
           ("Private.InternalDiagnostics.System.Net.Http", "Http")
           ("Private.InternalDiagnostics.System.Net.Ping", "Ping")
           ("Private.InternalDiagnostics.System.Net.Security", "Security")
           (* older .NET Core versions *)
           ("Microsoft-System-Net-Primitives", "Network")
           ("Microsoft-System-Net-NetworkInformation", "Network")
           ("Microsoft-System-Net-Sockets", "Socket")
           ("Microsoft-System-Net-NameResolution", "DNS")
           ("Microsoft-System-Net-Mail", "Mail")
           ("Microsoft-System-Net-Requests", "Request")
           ("Microsoft-System-Net-WinHttpHandler", "WinHttp")
           ("Microsoft-System-Net-HttpListener", "HttpListener")
           ("Microsoft-System-Net-Http", "Http")
           ("Microsoft-System-Net-Ping", "Ping")
           ("Microsoft-System-Net-Security", "Security") |]
        |> Map.ofArray

    let isEventAccepted state providerName eventName =
        if providerName = DiagnosticSourceEventSourceName then
            if eventName = "Event"
               || eventName = "Activity2/Start"
               || eventName = "Activity2/Stop" then
                EventFilterResponse.AcceptEvent
            else
                EventFilterResponse.RejectEvent
        elif state.ProviderNamesWithAliases
             |> Map.containsKey providerName then
            if eventName = "DumpBuffer" then
                EventFilterResponse.RejectEvent
            else
                EventFilterResponse.AcceptEvent
        else
            EventFilterResponse.RejectProvider

    let subscribe (session: WTraceEventSource, idgen, state: obj) =
        let state = state :?> NetworkHandlerState

        let predicate =
            Func<string, string, EventFilterResponse>(isEventAccepted state)

        let handleEvent h = Action<_>(handleEvent session.EventLevel idgen state h)

        session.Dynamic.AddCallbackForProviderEvents(predicate, handleEvent handleNetworkEvent)

    let getProviderSpec lvl =
        let providers =
            providerNamesWithAliases
            |> Map.toArray
            |> Array.map fst
            |> Array.map (fun n -> EventPipeProvider(n, lvl, 0x7L))

        { Providers = providers
          DiagnosticSourceFilterAndPayloadSpecs =
              [| "HttpHandlerDiagnosticListener/System.Net.Http.Request@Activity2Start:-Request.RequestUri;Request.Method;Request.Version;Request.Headers"
                 "HttpHandlerDiagnosticListener/System.Net.Http.Response@Activity2Stop:Response.StatusCode" |]
          ExtensionLoggingFilterSpecs = Array.empty<string> }


let createEventHandler () =
    { GetProviderSpec = getProviderSpec
      Initialize =
          fun (proc, broadcast) ->
              { Broadcast = broadcast
                ProcessName = proc.ProcessName
                ProviderNamesWithAliases = providerNamesWithAliases }
              :> obj
      Subscribe = subscribe }
