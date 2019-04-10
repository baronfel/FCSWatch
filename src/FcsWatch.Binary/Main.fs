﻿[<AutoOpen>]
module FcsWatch.Binary.Main
open FcsWatch.Core
open System.IO
open FcsWatch.Core.Compiler
open FcsWatch.Core.FcsWatcher
open System
open Extensions
open FSharp.Compiler.SourceCodeServices

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


type BinaryConfig =
    { LoggerLevel: Logger.Level
      DevelopmentTarget: DevelopmentTarget
      WorkingDir: string
      NoBuild: bool
      UseEditFiles: bool
      WarmCompile: bool }

with 
    static member DefaultValue =
        { LoggerLevel = Logger.Level.Minimal 
          DevelopmentTarget = DevelopmentTarget.autoReloadProgram 
          WorkingDir = Directory.GetCurrentDirectory() 
          NoBuild = false
          UseEditFiles = false
          WarmCompile = true }

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
          NoBuild = config.NoBuild
          OtherFlags = [||]
          UseEditFiles = config.UseEditFiles
          ActionAfterStoppingWatcher = fun cache ->
            match config.DevelopmentTarget with 
            | DevelopmentTarget.AutoReload autoReload ->
                AutoReload.CrackedFsproj.tryKill autoReload cache.EntryCrackedFsproj
            | _ -> ()
        }

    let checker = FSharpChecker.Create()

    match config.DevelopmentTarget with 
    | DevelopmentTarget.AutoReload autoReload ->
        let compilerTmpEmitter = AutoReload.create autoReload
        let fcsWatcher =
            fcsWatcherAndCompilerTmpAgent checker compilerTmpEmitter compiler coreConfig (Some entryProjectFile)
            |> fst

        let cache = fcsWatcher.PostAndReply FcsWatcherMsg.GetCache

        AutoReload.CrackedFsproj.tryRun autoReload config.WorkingDir cache.EntryCrackedFsproj

        fcsWatcher

    | DevelopmentTarget.Debuggable debuggable ->
        let compilerTmpEmitter = DebuggingServer.create debuggable
        let fcsWatcher, compilerTmpEmitterAgent = fcsWatcherAndCompilerTmpAgent checker compilerTmpEmitter compiler coreConfig (Some entryProjectFile)
        DebuggingServer.startServer config.WorkingDir compilerTmpEmitterAgent
        fcsWatcher

let runFcsWatcher (config: BinaryConfig) entryProjectFile =
    let binaryFcsWatcher = binaryFcsWatcher config entryProjectFile
    Console.ReadLine() |> ignore
    binaryFcsWatcher.PostAndReply FcsWatcherMsg.ActionAfterStoppedWatch