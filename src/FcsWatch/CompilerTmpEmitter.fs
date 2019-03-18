module FcsWatch.CompilerTmpEmiiter
open FcsWatch.Types
open System.Threading.Tasks
open System
open System.Threading
open FcsWatch.CrackedFsproj
open Suave

type CompilerTask = CompilerTask of startTime: DateTime * task: Task<CompilerResult list>
with 
    member x.StartTime = 
        match x with 
        | CompilerTask(startTime = m) -> m
    member x.Task = 
        match x with 
        | CompilerTask(task = m) -> m

[<RequireQualifiedAccess>]
type CompilerTmpEmitterMsg =
    | IncrCompilingNum of int
    | DecrCompilingNum of int
    | AddTmp of string (*proj path*)
    | Emit of replyChannel: AsyncReplyChannel<WebPart>
    | AddTask of CompilerTask
    | UpdateCache of CrackedFsprojBundleCache
    | GetCompilerTmp of AsyncReplyChannel<Set<string>>

type CompilerTmpEmitterState =
    { CompilingNumber: int
      /// proj paths
      CompilerTmp: Set<string>
      EmitReplyChannels: AsyncReplyChannel<WebPart> list
      GetTmpReplyChannels: AsyncReplyChannel<Set<string>> list
      CompilerTasks: CompilerTask list
      CrackerFsprojFileBundleCache: CrackedFsprojBundleCache }
    
[<RequireQualifiedAccess>]
module CompilerTmpEmiiterState =
    let createEmpty cache = 
        { CompilingNumber = 0
          CompilerTmp = Set.empty
          EmitReplyChannels = []
          CompilerTasks = []
          CrackerFsprojFileBundleCache = cache
          GetTmpReplyChannels = [] }


    let tryEmit config cache compilerTmpEmiiterState =
        logger.Info "tryEmitAction: current emitReplyChannels number is %d" compilerTmpEmiiterState.EmitReplyChannels.Length

        match compilerTmpEmiiterState.CompilingNumber,compilerTmpEmiiterState.EmitReplyChannels with 
        | 0, h::t ->
            let replySuccess() = h.Reply (Successful.OK "fcswatch: Ready to debug") 

            let replyFailure errorText = h.Reply (RequestErrors.BAD_REQUEST errorText)

            logger.Info "Current valid compier task is %d" compilerTmpEmiiterState.CompilerTasks.Length

            match compilerTmpEmiiterState.CompilerTasks with
            | [] ->
                replySuccess()

                match config.DevelopmentTarget with 
                | DevelopmentTarget.Plugin plugin ->
                    Thread.Sleep(plugin.DebuggerAttachTimeDelay)

                    plugin.Calculate()
                | _ -> ()

                compilerTmpEmiiterState
            | _ ->
                let lastTasks = 
                    compilerTmpEmiiterState.CompilerTasks 
                    |> List.groupBy (fun compilerTask ->
                        compilerTask.Task.Result.[0].ProjPath
                    )
                    |> List.map (fun (projPath, compilerTasks) ->
                        compilerTasks |> List.maxBy (fun compilerTask -> compilerTask.StartTime)
                    )

                let allResults = lastTasks |> List.collect (fun task -> task.Task.Result)

                match List.tryFind (fun result -> result.ExitCode <> 0) allResults with 
                | Some result ->
                    let errorText =  
                        result.Errors 
                        |> Seq.map (fun error -> error.ToString())
                        |> String.concat "\n"

                    replyFailure errorText
                    { compilerTmpEmiiterState with EmitReplyChannels = [] } 

                | None ->

                    let projRefersMap = cache.ProjRefersMap

                    let projLevelMap = cache.ProjLevelMap

                    match config.DevelopmentTarget with 
                    | DevelopmentTarget.Plugin plugin ->
                        plugin.Unload()
                    | _ -> ()

                    compilerTmpEmiiterState.CompilerTmp
                    |> Seq.sortByDescending (fun projPath ->
                        projLevelMap.[projPath]
                    )
                    |> Seq.iter (fun projPath ->
                        let currentCrackedFsproj = cache.ProjectMap.[projPath]

                        CrackedFsproj.copyObjToBin currentCrackedFsproj

                        let refCrackedFsprojs = projRefersMap.[projPath]

                        refCrackedFsprojs |> Seq.sortByDescending (fun refCrackedFsproj ->
                            projLevelMap.[refCrackedFsproj.ProjPath]
                        )
                        |> Seq.iter (CrackedFsproj.copyFileFromRefDllToBin projPath)
                    )

                    replySuccess()

                    match config.DevelopmentTarget with 
                    | DevelopmentTarget.Plugin plugin ->
                        plugin.Load()
                        plugin.Calculate()
                    | _ -> ()
                    
                    { createEmpty cache with GetTmpReplyChannels = compilerTmpEmiiterState.GetTmpReplyChannels }
        | _ -> compilerTmpEmiiterState
        

let compilerTmpEmitter config (initialCache: CrackedFsprojBundleCache) = MailboxProcessor<CompilerTmpEmitterMsg>.Start(fun inbox ->
    let rec loop state = async {
        let cache = state.CrackerFsprojFileBundleCache        
        let traceMsg compilingNumber msg = 
            logger.Info "compilerTmpEmitter agent receive message %s,current compiling number is %d" msg compilingNumber

        let! msg = inbox.Receive()
        match msg with 
        | CompilerTmpEmitterMsg.DecrCompilingNum number ->
            let compilingNumber = state.CompilingNumber - number

            traceMsg compilingNumber "DecrCompilingNum"

            assert (state.CompilingNumber > 0)
            let newState = 
                {state with CompilingNumber = compilingNumber}
                |> CompilerTmpEmiiterState.tryEmit config cache

            return! loop newState    

        | CompilerTmpEmitterMsg.IncrCompilingNum number -> 
            let compilingNumber = state.CompilingNumber + number

            traceMsg compilingNumber "IncrCompilingNum"

            return! loop {state with CompilingNumber = compilingNumber } 

        | CompilerTmpEmitterMsg.AddTmp projectFile -> 
            let newCompilerTmp = state.CompilerTmp.Add projectFile

            return! loop {state with CompilerTmp = newCompilerTmp }        
        | CompilerTmpEmitterMsg.Emit replyChannel ->
            traceMsg state.CompilingNumber "Emit"

            let newState =
                {state with EmitReplyChannels = replyChannel :: state.EmitReplyChannels}
                |> CompilerTmpEmiiterState.tryEmit config cache

            return! loop newState
        | CompilerTmpEmitterMsg.AddTask task -> 
            traceMsg state.CompilingNumber "AddTask"
            return! loop {state with CompilerTasks = task:: state.CompilerTasks} 

        | CompilerTmpEmitterMsg.UpdateCache cache ->
            traceMsg state.CompilingNumber "Update cache"

            return! loop {state with CrackerFsprojFileBundleCache = cache} 
            
        | CompilerTmpEmitterMsg.GetCompilerTmp replyChannel ->
            traceMsg state.CompilingNumber "GetCompilerTmp"
            let newState = 
                {state with GetTmpReplyChannels = replyChannel :: state.GetTmpReplyChannels }
                |> CompilerTmpEmiiterState.tryEmit config cache
            return! loop newState
    }
    loop (CompilerTmpEmiiterState.createEmpty initialCache) 
)