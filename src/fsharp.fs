[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Ionide.VSCode.FSharp

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Import.VSCode
open Fable.Import.VSCode.Vscode
open Ionide.VSCode.Helpers
open Ionide.VSCode.FSharp
open Node.ChildProcess

let private logger =
    ConsoleAndOutputChannelLogger(Some "Main", Level.DEBUG, Some defaultOutputChannel, Some Level.DEBUG)

let private requiredExtensions =
    [ "ms-dotnettools.csharp" // VSCode C# extension
      "anysphere.csharp" // Cursor C# extension
      "muhammad-sammy.csharp" ] // Free/Libre C# extension

let private checkCSharpExtension () =
    requiredExtensions
    |> List.exists (fun extId -> extensions.getExtension extId |> Option.isSome)

type Api =
    { ProjectLoadedEvent: Event<DTO.Project>
      BuildProject: DTO.Project -> JS.Promise<string>
      BuildProjectFast: DTO.Project -> JS.Promise<string>
      GetProjectLauncher: OutputChannel -> DTO.Project -> (string list -> JS.Promise<ChildProcess>) option
      DebugProject: DTO.Project -> string[] -> JS.Promise<unit> }


let private activateLanguageServiceRestart (context: ExtensionContext) =
    let restart () =
        promise {
            logger.Debug("Restarting F# language service")
            do! LanguageService.stop ()
            do! LanguageService.start context
            do! Project.initWorkspace ()
        }

    commands.registerCommand ("fsharp.restartLanguageService", restart |> objfy2)
    |> context.Subscribe

let private doActivate (context: ExtensionContext) : JS.Promise<Api> =
    let solutionExplorer = "FSharp.enableTreeView" |> Configuration.get true

    let showExplorer = "FSharp.showExplorerOnStartup" |> Configuration.get false

    let tryActivate label activationFn =
        fun ctx ->
            try
                activationFn ctx
            with ex ->
                logger.Error $"Error while activating feature '{label}': {ex}"
                Unchecked.defaultof<_>

    LanguageService.start context
    |> Promise.catch (fun e -> logger.Error $"Error activating FSAC: %A{e}") // prevent unhandled rejected promises
    |> Promise.onSuccess (fun _ ->
        let progressOpts = createEmpty<ProgressOptions>
        progressOpts.location <- U2.Case1 ProgressLocation.Window
        logger.Debug "Activating features"

        window.withProgress (
            progressOpts,
            (fun p ctok ->
                let pm =
                    {| message = Some "Loading projects"
                       increment = None |}

                p.report pm

                Project.activate context
                |> Promise.catch (fun e -> logger.Error $"Error loading projects: %A{e}")
                |> Promise.onSuccess (fun _ -> tryActivate "quickinfoproject" QuickInfoProject.activate context)
                |> Promise.bind (fun _ ->
                    if showExplorer then
                        commands.executeCommand (VSCodeExtension.workbenchViewId ())
                        |> Promise.ofThenable
                    else
                        Promise.lift None)
                |> Promise.bind (fun _ -> tryActivate "analyzers" LanguageService.loadAnalyzers ())
                |> Promise.catch (fun e ->
                    logger.Error $"Error loading all projects: %A{e}"

                    let pm =
                        {| message = Some "Error loading projects"
                           increment = None |}

                    p.report pm)
                |> Promise.toThenable)
        )
        |> ignore)
    |> Promise.map (fun _ ->
        if solutionExplorer then
            tryActivate "solutionExplorer" SolutionExplorer.activate context

        tryActivate "fsprojedit" FsProjEdit.activate context
        tryActivate "diagnostics" Diagnostics.activate context
        tryActivate "linelens" LineLens.Instance.activate context
        tryActivate "quickinfo" QuickInfo.activate context
        tryActivate "help" Help.activate context
        tryActivate "msbuild" MSBuild.activate context
        tryActivate "signaturedata" SignatureData.activate context
        tryActivate "debugger" Debugger.activate context
        tryActivate "fsdn" Fsdn.activate context
        tryActivate "fsi" Fsi.activate context
        tryActivate "scriptrunner" ScriptRunner.activate context
        tryActivate "languageconfiguration" LanguageConfiguration.activate context
        tryActivate "htmlconverter" HtmlConverter.activate context
        tryActivate "infopanel" InfoPanel.activate context
        tryActivate "codelens" CodeLensHelpers.activate context
        tryActivate "gitignore" Gitignore.activate context
        tryActivate "pipelinehints" PipelineHints.Instance.activate context
        tryActivate "testExplorer" TestExplorer.activate context
        tryActivate "inlayhints" InlayHints.activate context
        tryActivate "languageservice" activateLanguageServiceRestart context

        let buildProject project =
            promise {
                let! exit = MSBuild.buildProjectPath "Build" project

                match exit.Code with
                | Some code -> return code.ToString()
                | None -> return ""
            }

        let buildProjectFast project =
            promise {
                let! exit = MSBuild.buildProjectPathFast project

                match exit.Code with
                | Some code -> return code.ToString()
                | None -> return ""
            }

        let event = vscode.EventEmitter.Create<DTO.Project>()

        Project.projectLoaded.Invoke(fun n -> !!(setTimeout (fun _ -> event.fire n) 500.))
        |> ignore

        { ProjectLoadedEvent = event.event
          BuildProject = buildProject
          BuildProjectFast = buildProjectFast
          GetProjectLauncher = Project.getLauncher
          DebugProject = Debugger.debugProject })
    |> Promise.catch (fun e ->
        logger.Error $"Error activating features: %A{e}"
        Unchecked.defaultof<_>)

let activate (context: ExtensionContext) : JS.Promise<Api> =
    // Check for C# extension at runtime
    if not (checkCSharpExtension ()) then
        let extensionList =
            requiredExtensions
            |> List.rev
            |> function
                | [] -> ""
                | [ x ] -> x
                | last :: rest ->
                    let restStr = rest |> List.rev |> String.concat ", "
                    $"{restStr} or {last}"

        window.showErrorMessage ($"Ionide requires one of the following C# extensions to be installed: {extensionList}")
        |> Promise.ofThenable
        |> Promise.map (fun _ -> Unchecked.defaultof<_>)
    else
        doActivate context


let deactivate (disposables: Disposable[]) = LanguageService.stop ()
