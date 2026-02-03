open System
open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.NameSilo

[<EntryPoint>]
let main args =
    let apiKey = Environment.GetEnvironmentVariable NameSiloProvider.ApiKeyEnvVarName
    // Allow empty token for cases when provider is used to get schema for SDK.
    // Do a check in NameSiloProvider.Configure method instead.
    Provider.Serve(args, NameSiloProvider.Version, (fun _host -> new NameSiloProvider(apiKey)), CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    0
