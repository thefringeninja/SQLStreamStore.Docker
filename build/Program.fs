open System
open System.IO
open System.Runtime.InteropServices
open Bullseye;
open SimpleExec

let ArtifactsDir = "artifacts"
let PublishDir = "publish"
let Clean = "Clean"
let SetupDocumentation = "SetupDocumentation"
let GenerateDocumentation = "GenerateDocumentation"
let Build = "Build"
let RunTests = "RunTests"
let Publish = "Publish"
let Pack = "Pack"

let orEmpty (s : Option<string>) =
    match s with
    | Some s -> s
    | None -> ""

let srcDirectory = DirectoryInfo "./src"

let cleanDirectory path : unit =
    if Directory.Exists path then Directory.Delete(path, true)
    |> ignore

let env (key : string) : Option<string> =
    let env = Environment.GetEnvironmentVariable(key)
    match String.IsNullOrEmpty env with
    | true -> Some env
    | false -> None

let apiKey = env "MYGET_API_KEY"

let buildNumber =
    match env "TRAVIS_BUILD_NUMBER" with
    | Some s -> s | None -> "0"

let commitHash =
    match env "TRAVIS_PULL_REQUEST_SHA" with
    | Some s -> s
    | None -> match env "TRAVIS_COMMIT" with
              | Some s -> s
              | None -> "none"

let branch =
    let pr = orEmpty (env "TRAVIS_PULL_REQUEST")
    match env "TRAVIS_PULL_REQUEST" with
    | Some "false" -> ""
    | None -> match env "TRAVIS_BRANCH" with
              | Some s -> s
              | None -> "none"
    | _ -> sprintf "pr-%s" pr

let buildMetadata = sprintf "%s.%s" branch commitHash

let generateDoc sd =
    Command.Run ("node",
        sprintf "node_modules/@adobe/jsonschema2md/cli.js -n --input %s --out %s --schema-out=-" sd sd,
        "docs") |> ignore

let dotnet (cmd : string) (project : Option<string>) (args : Option<string>) (workingDiretory: Option<string>) =
    Command.Run ("dotnet",
        sprintf "%s %s --configuration=Release %s /p:BuildNumber=%s /p:BuildMetadata=%s" cmd (orEmpty project) (orEmpty args) buildNumber buildMetadata,
        if workingDiretory.IsNone then null else workingDiretory.Value)

let schemaDirectories =
    srcDirectory.GetFiles("*.schema.json", SearchOption.AllDirectories)
    |> Seq.map (fun d -> d.DirectoryName)
    |> Seq.distinct
    |> Seq.map (fun dn -> dn.Replace(Path.DirectorySeparatorChar, '/'))

let packages = seq { for d in Directory.GetFiles(ArtifactsDir, "*.nupkg", SearchOption.TopDirectoryOnly) do yield d } 
    
let pushPackage (p : string) =
    match apiKey with
    | Some s ->
        let args = Some(sprintf "--source https://www.myget.org/F/sqlstreamstore/api/v3/index.json -k %s" s)
        dotnet "nuget push" (Some p) args |> ignore
    | None -> printf "MyGet API key not available. Package %s will not be pushed" p

let target (t : string) (d : seq<string>) (a : unit -> unit) =
    Targets.Target(t, d, a)

let foreachTarget<'a> (f : seq<'a>) (t : string) (d : seq<string>) (a : 'a -> unit) =
    Targets.Target(t, d, f, a)

let defaultTarget (d : seq<string>) =
    Targets.Target("default", d)

let runTargets args =
    Targets.RunTargets args

[<EntryPoint>]
let main argv =
    target
        Clean [] (fun () ->
        cleanDirectory ArtifactsDir
        cleanDirectory PublishDir)

    target
        SetupDocumentation [] (fun () ->
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then Command.Run("cmd", "/c yarn", "docs")
        else Command.Run("yarn", "", "docs"))

    foreachTarget schemaDirectories
        GenerateDocumentation [ SetupDocumentation ] generateDoc

    target
        Build [ GenerateDocumentation ] (fun () ->
        dotnet "build" (Some "src/SqlStreamStore.HAL.sln") |> ignore)

    target
        RunTests [ Build ] (fun () ->
        let args = Some (sprintf "--results-directory %s --verbosity normal --no-build --logger trx;LogFileName=SqlStreamStore.HAL.Tests.xml" ArtifactsDir)
        dotnet "test" (Some "src/SqlStreamStore.HAL.Tests") args |> ignore)

    target
        Publish [ Build ] (fun () ->
        let args = Some(sprintf "--output=../../%s --runtime=alpine.3.7-x64 /p:ShowLinkerSizeComparison=true" PublishDir)
        dotnet "publish" None args (Some "src/SqlStreamStore.HAL.DevServer") |> ignore)

    foreachTarget packages
        Pack [ Build ] pushPackage

    defaultTarget [
        Clean;
        RunTests;
        Publish;
        Pack
    ]

    runTargets argv

    0
