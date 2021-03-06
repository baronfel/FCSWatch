﻿[<AutoOpen>]
module FcsWatch.Binary.Main
open FcsWatch.Core
open System.IO
open FcsWatch.Core.Compiler
open FcsWatch.Core.FcsWatcher
open System
open Extensions
open FSharp.Compiler.SourceCodeServices
open Fake.IO.FileSystemOperators
open FcsWatch.Core.Types

[<RequireQualifiedAccess>]
type DevelopmentTarget =
    | Debuggable of DebuggingServer.DevelopmentTarget
    | AutoReload of AutoReload.DevelopmentTarget

[<RequireQualifiedAccess>]
module DevelopmentTarget =
    let debuggableProgram = DevelopmentTarget.Debuggable DebuggingServer.DevelopmentTarget.Program
    let autoReloadProgram = DevelopmentTarget.AutoReload AutoReload.DevelopmentTarget.Program
    let autoReloadPlugin plugin = DevelopmentTarget.AutoReload (AutoReload.DevelopmentTarget.Plugin plugin)
    let debuggablePlugin plugin = DevelopmentTarget.Debuggable (DebuggingServer.DevelopmentTarget.Plugin plugin)

    let (|Plugin|Program|) = function
        | DevelopmentTarget.AutoReload autoReload ->
            match autoReload with
            | AutoReload.DevelopmentTarget.Plugin plugin -> Plugin plugin
            | AutoReload.DevelopmentTarget.Program _ -> Program

        | DevelopmentTarget.Debuggable debuggable ->
            match debuggable with
            | DebuggingServer.DevelopmentTarget.Plugin plugin -> Plugin (DebuggingServer.Plugin.asAutoReloadPlugin plugin)
            | DebuggingServer.DevelopmentTarget.Program _ -> Program


type BinaryConfig =
    { LoggerLevel: Logger.Level
      DevelopmentTarget: DevelopmentTarget
      WorkingDir: string
      NoBuild: bool
      Framework: string option
      Configuration: Configuration
      UseEditFiles: bool
      Webhook: string option
      WarmCompile: bool
      AdditionalSwitchArgs : string array }

with
    static member DefaultValue =
        { LoggerLevel = Logger.Level.Minimal
          DevelopmentTarget = DevelopmentTarget.autoReloadProgram
          WorkingDir = Directory.GetCurrentDirectory()
          NoBuild = false
          Framework = None
          Configuration = Configuration.Debug
          UseEditFiles = false
          WarmCompile = true
          Webhook = None
          AdditionalSwitchArgs = Array.empty }


[<RequireQualifiedAccess>]
module BinaryConfig =

    let additionalBuildArgs (config: BinaryConfig) =
        match config.Configuration with
        | Configuration.Release -> ["--configuration"; "Release"]
        | Configuration.Debug -> ["--configuration"; "Debug"]

    let asAutoReloadProgramRunningArgs whyRun autoReloadDevelopmentTarget (config: BinaryConfig) : AutoReload.ProgramRunningArgs =
        { AdditionalSwitchArgs = List.ofArray config.AdditionalSwitchArgs
          Webhook = config.Webhook
          DevelopmentTarget = autoReloadDevelopmentTarget
          WorkingDir = config.WorkingDir
          Framework = config.Framework
          AdditionalBuildArgs = additionalBuildArgs config
          Configuration = config.Configuration
          WhyRun = whyRun }


    let tryBuildProject projectFile (config: BinaryConfig) =
        if not config.NoBuild then
            let additionalBuildArgs = additionalBuildArgs config

            match config.DevelopmentTarget with
            | DevelopmentTarget.Program _ ->
                dotnet "build" ([projectFile] @ additionalBuildArgs) config.WorkingDir

            | DevelopmentTarget.Plugin plugin ->
                plugin.Unload()
                dotnet "build" ([projectFile] @ additionalBuildArgs) config.WorkingDir

let binaryFcsWatcher (config: BinaryConfig) entryProjectFile =

    logger <- Logger.create config.LoggerLevel

    let config =
        { config with
            WorkingDir = Path.GetFullPath(config.WorkingDir)}

    let compiler =
        let summary projectPath dll elapsed =
            sprintf "Summary:
        -- origin: %s
        -- dest： %s
        -- elapsed: %d milliseconds"
                projectPath dll elapsed

        { new ICompiler<CompilerResult> with
            member x.Compile(checker, crackedFsproj) = CrackedFsproj.compile checker crackedFsproj
            member x.WarmCompile = config.WarmCompile
            member x.Summary (result, elapsed) = summary result.ProjPath result.Dll elapsed}

    let coreConfig: FcsWatch.Core.Config =
        { LoggerLevel = config.LoggerLevel
          WorkingDir = config.WorkingDir
          OtherFlags = [||]
          Configuration = config.Configuration
          UseEditFiles = config.UseEditFiles }

    let checker = FSharpChecker.Create()

    BinaryConfig.tryBuildProject entryProjectFile config

    match config.DevelopmentTarget with
    | DevelopmentTarget.AutoReload autoReload ->

        let programRunningArgs: AutoReload.ProgramRunningArgs =
            BinaryConfig.asAutoReloadProgramRunningArgs WhyRun.Run autoReload config

        let compilerTmpEmitter = AutoReload.create programRunningArgs

        let fcsWatcher =
            fcsWatcherAndCompilerTmpAgent checker compilerTmpEmitter compiler coreConfig (Some entryProjectFile)
            |> fst

        let cache = fcsWatcher.PostAndReply FcsWatcherMsg.GetCache

        AutoReload.CrackedFsproj.tryRun programRunningArgs cache.EntryCrackedFsproj

        fcsWatcher

    | DevelopmentTarget.Debuggable debuggable ->
        let compilerTmpEmitter = DebuggingServer.create debuggable
        let fcsWatcher, compilerTmpEmitterAgent = fcsWatcherAndCompilerTmpAgent checker compilerTmpEmitter compiler coreConfig (Some entryProjectFile)
        DebuggingServer.startServer config.WorkingDir compilerTmpEmitterAgent
        fcsWatcher

let tryKill autoReloadDevelopmentTarget entryCrackedFsproj =
    AutoReload.CrackedFsproj.tryKill autoReloadDevelopmentTarget entryCrackedFsproj

let runFcsWatcher (processExit : System.Threading.Tasks.Task<unit>) (config: BinaryConfig) entryProjectFile = async {
    let binaryFcsWatcher = binaryFcsWatcher config entryProjectFile
    do! processExit |> Async.AwaitTask
    let cache = binaryFcsWatcher.PostAndReply FcsWatcherMsg.GetCache
    match config.DevelopmentTarget with
    | DevelopmentTarget.AutoReload autoReload -> tryKill autoReload cache.EntryCrackedFsproj
    | _ -> ()

    logger.Info "Exited fcswatch.exe"
}
