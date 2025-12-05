namespace Pulumi.NameSilo

open System
open System.Collections.Immutable
open System.Linq
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open Pulumi
open Pulumi.Experimental
open Pulumi.Experimental.Provider

type NameSiloProvider(apiKey: string) =
    inherit Pulumi.Experimental.Provider.Provider()

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
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> = 
        let schema =
            sprintf
                """{
                    "name": "sherlockdomains",
                    "version": "%s",
                    "resources": {
                    },
                    "provider": {
                    }
                }"""
                NameSiloProvider.Version

        Task.FromResult <| GetSchemaResponse(Schema = schema)

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        // TODO: check API key
        Task.FromResult <| ConfigureResponse()

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        // TODO: implement
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        // TODO: check reqest type
        let diff = request.NewInputs.Except request.OldInputs 
        let replaces = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
        Task.FromResult <| DiffResponse(Changes = (replaces.Length > 0), Replaces = replaces)

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            return failwith "Not implemented"
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            return failwith "Not implemented"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            return failwith "Not implemented"
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            return failwith "Not implemented"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
