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

    interface IDisposable with
        override self.Dispose (): unit = 
            httpClient.Dispose()

    member private self.GetDnsRecordPropertyString(dict: ImmutableDictionary<string, PropertyValue>, name: string) =
        match dict.[name].TryGetString() with
        | true, value -> value
        | false, _ -> failwith $"No {name} property in {dnsRecordResourceName}"

    member private self.GetDnsRecordPropertyInt(dict: ImmutableDictionary<string, PropertyValue>, name: string) =
        match dict.[name].TryGetNumber() with
        | true, value -> int value
        | false, _ -> failwith $"No {name} property in {dnsRecordResourceName}"

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

            let! response = self.RequestAsync(endpoint, finalParameters)
            let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                
            if response.StatusCode <> HttpStatusCode.OK then
                return failwith $"Namesilo server returned error ({response.StatusCode}). Response: {responseContent}"
            else
                return JsonDocument.Parse(responseContent).RootElement.GetProperty("reply").GetProperty("record_id").GetString()
        }

    member private self.RequestAsync(endpoint: string, parameters: Map<string, string>): Async<HttpResponseMessage> =
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

        let schema =
            sprintf
                """{
                    "name": "namesilo",
                    "version": "%s",
                    "resources": {
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        }
                    },
                    "provider": {
                    }
                }"""
                NameSiloProvider.Version
                dnsRecordResourceName
                dnsRecordProperties
                dnsRecordProperties
        
        Task.FromResult <| GetSchemaResponse(Schema = schema)

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        if String.IsNullOrWhiteSpace apiKey then
            failwith $"Environment variable {NameSiloProvider.ApiKeyEnvVarName} not provided."
        Task.FromResult <| ConfigureResponse()

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        if request.Type = dnsRecordResourceName then
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs)
        else
            failwith $"Unknown resource type '{request.Type}'"

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type = dnsRecordResourceName then
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
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            if request.Type = dnsRecordResourceName then
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                let uri = $"{apiBaseUrl}/api/v0/domains/{domainId}/dns/records/{request.Id}"
                let! response = httpClient.DeleteAsync(uri) |> Async.AwaitTask
                
                if response.StatusCode <> HttpStatusCode.OK then
                    let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    eprintfn "Failed to delete %s with id=%s in domain with domainId=%s" request.Type request.Id domainId
                    let errorMessage = $"SherlockDomains server returned error ({response.StatusCode}). Response: {responseContent}"
                    if responseContent.Contains "record_id missing" then
                        eprintfn "%s" errorMessage
                    else
                        failwith errorMessage
                else
                    ()
            else
                failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            if request.Type = dnsRecordResourceName then
                let domainId = self.GetDnsRecordPropertyString(request.Properties, "domainId")
                let! response = self.RequestAsync("dnsListRecords", Map.ofList [ "domain", domainId ])
                
                if response.StatusCode <> HttpStatusCode.OK then
                    return failwith $"Namesilo server returned error (code {response.StatusCode})"
                else
                    let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let records = JsonDocument.Parse(responseJson).RootElement.GetProperty("reply").GetProperty("resource_record")
                    match records.EnumerateArray() |> Seq.tryFind(fun record -> record.GetProperty("record_id").GetString() = request.Id) with
                    | Some record ->
                        let properties = 
                            [ for prop in record.EnumerateObject() do 
                                  if request.Properties.ContainsKey prop.Name then
                                      let value = 
                                          if prop.Value.ValueKind = JsonValueKind.String then
                                              PropertyValue(prop.Value.GetString())
                                          elif prop.Value.ValueKind = JsonValueKind.Number then
                                              PropertyValue(prop.Value.GetInt32())
                                          else
                                              failwith $"Unexpected type: {prop.Value.ValueKind}"
                                      yield prop.Name, value ]
                            |> dict
                        return ReadResponse(Id = request.Id, Properties = properties)
                    | None -> 
                        return failwith $"Record with id={request.Id} not found"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
