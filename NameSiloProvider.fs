namespace Pulumi.NameSilo

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Linq
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Web

open Pulumi
open Pulumi.Experimental
open Pulumi.Experimental.Provider

type private DnsRecordOperation =
    | Update of id: string
    | Add

type NameSiloProvider(apiKey: string) =
    inherit Pulumi.Experimental.Provider.Provider()

    static let dnsRecordResourceName = "namesilo:index:DnsRecord"
    static let nameserversResourceName = "namesilo:index:Nameservers"
    static let apiBaseUrl = "https://www.namesilo.com/api"

    let httpClient = new HttpClient()

    // Provider has to advertise its version when outputting schema, e.g. for SDK generation.
    // In pulumi-bitlaunch, we have Pulumi generate the terraform bridge, and it automatically pulls version from the tag.
    // Use sdk/dotnet/version.txt as source of version number.
    // WARNING: that file is deleted when SDK is generated using `pulumi package gen-sdk` command; it has to be re-created.
    static member val Version = 
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()
        let resourceName = 
            assembly.GetManifestResourceNames()
            |> Seq.find (fun str -> str.EndsWith "version.txt")
        use stream = assembly.GetManifestResourceStream resourceName
        use reader = new System.IO.StreamReader(stream)
        reader.ReadToEnd().Trim()

    static member val ApiKeyEnvVarName = "NAMESILO_API_KEY"

    member val DefaultNameservers: string * string = String.Empty, String.Empty with get, set

    interface IDisposable with
        override self.Dispose (): unit = 
            httpClient.Dispose()

    member private self.GetPropertyString (resourceName: string) (dict: ImmutableDictionary<string, PropertyValue>) (name: string) =
        match dict.TryGetValue name with
        | true, propertyValue ->
            match propertyValue.TryGetString() with
            | true, value -> value
            | false, _ -> failwith $"Value of property {name} ({propertyValue}) in {resourceName} is not a string"
        | false, _ -> failwith $"No {name} property in {resourceName}"

    member private self.GetDnsRecordPropertyString(dict: ImmutableDictionary<string, PropertyValue>, name: string) =
        self.GetPropertyString dnsRecordResourceName dict name

    member private self.ReportErrorInResponse (responseBody: string) (errorMessage: string) =
        failwithf
            "%s.%sFull reply: %s"
            errorMessage
            Environment.NewLine
            responseBody

    /// Parse response body as JSON and get "reply" element. If repsonse code indicates error, raise an exception.
    member private self.ParseResponseAndGetReply(responseBody: string) =
        let json = JsonDocument.Parse(responseBody).RootElement
        match json.TryGetProperty "reply" with
        | true, reply ->
            match reply.TryGetProperty "code" with
            | true, codeElement ->
                let code = 
                    match codeElement.TryGetInt32() with
                    | true, number -> number
                    | false, _ -> codeElement.ToString() |> int
                if code < 300 || code >= 400 then
                    self.ReportErrorInResponse responseBody $"Reply code does not indicate success: {code}" 
                else
                    reply
            | false, _ ->
                self.ReportErrorInResponse responseBody "Reply JSON does not contain 'code' element in 'reply' dictionary"

        | false, _ ->
            self.ReportErrorInResponse responseBody "Reply JSON does not contain 'reply' element"

    member private self.AsyncUpdateOrCreateDnsRecord(operation: DnsRecordOperation, properties: ImmutableDictionary<string, PropertyValue>): Async<string> =
        async {
            let maybeId, endpoint =
                match operation with
                | Add -> None, "dnsAddRecord"
                | Update(id) -> Some id, "dnsUpdateRecord"

            let parameters = 
                properties
                |> Seq.map (fun keyValuePair -> keyValuePair.Key, keyValuePair.Value.ToString())
                |> Map.ofSeq

            let finalParameters =
                match maybeId with
                | Some id -> parameters |> Map.add "rrid" id
                | None -> parameters

            let! response = self.AsyncRequest(endpoint, finalParameters)
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"Namesilo server returned error ({response.StatusCode}). Response: {responseContent}"
            else
                let reply = self.ParseResponseAndGetReply responseContent
                match reply.TryGetProperty "record_id" with
                | true, recordId ->
                    return recordId.ToString()
                | false, _ ->
                    return 
                        failwithf 
                            "Reply JSON does not contain 'record_id' element in 'reply' dictionary. Full reply: %s%sRequest params: %A"
                            responseContent
                            Environment.NewLine
                            finalParameters
        }

    member private self.AsyncChangeNameservers (domain: string) (ns1: string) (ns2: string): Async<unit> =
        async {
            let parameters = 
                Map.ofList
                    [
                        "domain", domain
                        "ns1", ns1
                        "ns2", ns2
                    ]
            let! response = self.AsyncRequest("changeNameServers", parameters)
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"Namesilo server returned error ({response.StatusCode}). Response: {responseContent}"
            else
                ignore <| self.ParseResponseAndGetReply responseContent
        }

    member private self.AsyncRequest(endpoint: string, parameters: Map<string, string>): Async<HttpResponseMessage> =
        let parametersString = 
            String.Join(
                String.Empty,
                parameters 
                |> Seq.map (fun param -> $"&{HttpUtility.UrlEncode param.Key}={HttpUtility.UrlEncode param.Value}")
            )
        let uri = $"{apiBaseUrl}/{endpoint}?version=1&type=json&key={apiKey}{parametersString}"
        httpClient.GetAsync uri |> Async.AwaitTask
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> =
        let dnsRecordProperties = 
            """{
                                "domain": {
                                    "type": "string",
                                    "description": "The domain the record belongs to."
                                },
                                "rrtype": {
                                    "type": "string",
                                    "description": "The type of resources record to add. Possible values are A, AAAA, CNAME, MX, TXT, SRV and CAA"
                                },
                                "rrhost": {
                                    "type": "string",
                                    "description": "The hostname for the new record (there is no need to include the .DOMAIN)"
                                },
                                "rrvalue": {
                                    "type": "string",
                                    "description": "The value for the resource record"
                                },
                                "rrttl": {
                                    "type": "integer",
                                    "description": "The TTL for the new record (default is 7207 if not provided)"
                                }
                            }"""

        let nameserversProperties = 
            """{
                                "domain": {
                                    "type": "string",
                                    "description": "The domain the nameservers are associated with."
                                },
                                "ns1": {
                                    "type": "string",
                                    "description": "Nameserver 1"
                                },
                                "ns2": {
                                    "type": "string",
                                    "description": "Nameserver 2"
                                }
                            }"""

        let schema =
            sprintf
                """{
                    "name": "namesilo",
                    "version": "%s",
                    "resources": {
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        },
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s,
                            "requiredInputs": [ "domain", "ns1", "ns2" ]
                        }
                    },
                    "provider": {
                        "inputProperties": {
                            "defaultNs1": {
                                "type": "string",
                                "description": "Default nameserver to be used in delete operation 1"
                            },
                            "defaultNs2": {
                                "type": "string",
                                "description": "Default nameserver to be used in delete operation 1"
                            }
                        },
                        "requiredInputs": [ "defaultNs1", "defaultNs2" ]
                    }
                }"""
                NameSiloProvider.Version
                dnsRecordResourceName
                dnsRecordProperties
                dnsRecordProperties
                nameserversResourceName
                nameserversProperties
                nameserversProperties
        
        Task.FromResult <| GetSchemaResponse(Schema = schema)

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        if String.IsNullOrWhiteSpace apiKey then
            failwith $"Environment variable {NameSiloProvider.ApiKeyEnvVarName} not provided."
        let ns1 = self.GetPropertyString "provider args" request.Args "defaultNs1"
        let ns2 = self.GetPropertyString "provider args" request.Args "defaultNs2"
        self.DefaultNameservers <- ns1, ns2
        Task.FromResult <| ConfigureResponse()

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        if request.Type = dnsRecordResourceName || request.Type = nameserversResourceName then
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs)
        else
            failwith $"Unknown resource type '{request.Type}'"

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type = dnsRecordResourceName || request.Type = nameserversResourceName then
            let diff = request.NewInputs.Except request.OldInputs 
            let replaces = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
            Task.FromResult <| DiffResponse(Changes = (replaces.Length > 0), Replaces = replaces)
        else
            failwith $"Unknown resource type '{request.Type}'"

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let! id = self.AsyncUpdateOrCreateDnsRecord(Add, request.Properties)
                return CreateResponse(Id = id, Properties = request.Properties)
            elif request.Type = nameserversResourceName then
                let domain = self.GetPropertyString nameserversResourceName request.Properties "domain"
                let ns1 = self.GetPropertyString nameserversResourceName request.Properties "ns1"
                let ns2 = self.GetPropertyString nameserversResourceName request.Properties "ns2"
                do! self.AsyncChangeNameservers domain ns1 ns2
                return CreateResponse(Id = Guid.NewGuid().ToString(), Properties = request.Properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let properties = request.Olds.AddRange request.News
                do! self.AsyncUpdateOrCreateDnsRecord(Update(request.Id), properties) |> Async.Ignore<string>
                return UpdateResponse(Properties = properties)
            elif request.Type = nameserversResourceName then
                if request.Olds.["domain"] <> request.News.["domain"] then
                    failwith $"Cannot update 'domain' property of resource {nameserversResourceName}. Instead destroy old resource and create a new one."
                let properties = request.Olds.AddRange request.News
                let domain = self.GetPropertyString nameserversResourceName properties "domain"
                let ns1 = self.GetPropertyString nameserversResourceName properties "ns1"
                let ns2 = self.GetPropertyString nameserversResourceName properties "ns2"
                do! self.AsyncChangeNameservers domain ns1 ns2
                return UpdateResponse(Properties = properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            if request.Type = dnsRecordResourceName then
                let domain = self.GetDnsRecordPropertyString(request.Properties, "domain")
                let rrid = request.Id
                let! response = self.AsyncRequest("dnsDeleteRecord", Map.ofList [ "domain", domain; "rrid", rrid ])
                
                if response.StatusCode <> HttpStatusCode.OK then
                    let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    eprintfn "Failed to delete %s with id=%s in domain %s" request.Type request.Id domain
                    failwith $"Namesilo server returned error ({response.StatusCode}). Response: {responseContent}"
                else
                    ()
            elif request.Type = nameserversResourceName then
                let domain = self.GetPropertyString nameserversResourceName request.Properties "domain"
                let ns1, ns2 = self.DefaultNameservers
                do! self.AsyncChangeNameservers domain ns1 ns2
            else
                failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let domain = self.GetDnsRecordPropertyString(request.Properties, "domain")
                let! response = self.AsyncRequest("dnsListRecords", Map.ofList [ "domain", domain ])
                
                if response.StatusCode <> HttpStatusCode.OK then
                    return failwith $"Namesilo server returned error (code {response.StatusCode})"
                else
                    let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let reply = self.ParseResponseAndGetReply responseBody
                    match reply.TryGetProperty "resource_record" with
                    | true, records ->
                        let maybeRecord = 
                            records.EnumerateArray()
                            |> Seq.tryFind
                                (fun record -> 
                                    match record.TryGetProperty "record_id" with
                                    | true, recordId -> recordId.GetString() = request.Id
                                    | false, _ ->
                                        self.ReportErrorInResponse
                                            responseBody
                                            "Could not get 'record_id' property in element of 'resource_record' array")

                        match maybeRecord with
                        | Some record ->
                            let properties = 
                                dict [ 
                                    "rrtype", record.GetProperty("type").GetString() |> PropertyValue
                                    "rrhost", record.GetProperty("host").GetString() |> PropertyValue
                                    "rrvalue", record.GetProperty("value").GetString() |> PropertyValue
                                    "rrttl", record.GetProperty("ttl").GetInt32() |> PropertyValue
                                ]
                            return ReadResponse(Id = request.Id, Properties = properties)
                        | None -> 
                            return failwith $"Record with id={request.Id} not found"
                    | false, _ ->
                        return
                            self.ReportErrorInResponse
                                responseBody
                                "Could not get 'resource_record' in reply dict while getting list of DNS records"
            elif request.Type = nameserversResourceName then
                let domain = self.GetPropertyString nameserversResourceName request.Properties "domain"
                let! response = self.AsyncRequest("getDomainInfo", Map.ofList [ "domain", domain ])
                
                if response.StatusCode <> HttpStatusCode.OK then
                    return failwith $"Namesilo server returned error (code {response.StatusCode})"
                else
                    let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let reply = self.ParseResponseAndGetReply responseBody
                    match reply.TryGetProperty "nameservers" with
                    | true, nameservers ->
                        let nameservers =
                            nameservers.EnumerateArray()
                            |> Seq.sortBy (fun nsRecord -> nsRecord.GetProperty("position").GetInt32())
                            |> Seq.take 2
                            |> Seq.map (fun nsRecord -> nsRecord.GetProperty("nameserver").GetString())
                            |> Seq.toList
                        let properties = 
                            dict [ 
                                "domain", PropertyValue domain
                                "ns1", PropertyValue nameservers.[0]
                                "ns2", PropertyValue nameservers.[1]
                            ]
                        return ReadResponse(Id = request.Id, Properties = properties)
                    | false, _ ->
                        return
                            self.ReportErrorInResponse
                                responseBody
                                "Could not get 'nameservers' in reply dict while getting list of DNS records"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
