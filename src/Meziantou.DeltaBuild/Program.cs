using System.CommandLine;
using Microsoft.Build.Locator;

namespace Meziantou.DeltaBuild;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Must register MSBuild BEFORE any Microsoft.Build types are loaded
        MSBuildLocator.RegisterDefaults();

        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        var inputOption = new Option<string>("--input", "-i")
        {
            Description = "Path to the input file (SLN, SLNX, Traversal SDK, or single project file)",
            Required = true,
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Path for the output file (SLN, SLNX, or Traversal SDK). Format is inferred from extension.",
            Required = true,
        };

        var repositoryOption = new Option<string>("--repository", "-r")
        {
            Description = "Path to the git repository (default: current directory)",
            DefaultValueFactory = _ => ".",
        };

        var headCommitOption = new Option<string?>("--head-commit")
        {
            Description = "The head commit SHA (default: HEAD)",
        };

        var baseCommitOption = new Option<string?>("--base-commit")
        {
            Description = "The base commit SHA (default: auto-detected via merge-base)",
        };

        var baseBranchOption = new Option<string?>("--base-branch")
        {
            Description = "The base branch for merge-base detection (default: auto-detected from GitHub Actions PR context or remote)",
        };

        var workingTreeOption = new Option<bool>("--working-tree")
        {
            Description = "Compare the base commit against the current working directory instead of a commit. Includes staged, unstaged, and untracked files. When set, --head-commit is ignored.",
        };

        var includeOption = new Option<string[]>("--include")
        {
            Description = "Glob patterns to filter which projects to consider (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        var fullRebuildTriggerOption = new Option<string[]>("--full-rebuild-trigger")
        {
            Description = "Glob patterns for files that trigger a full rebuild of ALL projects (replaces defaults when provided, repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        var hierarchicalRebuildTriggerOption = new Option<string[]>("--hierarchical-rebuild-trigger")
        {
            Description = "Glob patterns for files that trigger a rebuild of projects in the same folder hierarchy (replaces defaults when provided, repeatable). Default: **/global.json, **/nuget.config, **/.editorconfig",
            AllowMultipleArgumentsPerToken = true,
        };

        var engineOption = new Option<AnalysisEngine>("--engine")
        {
            Description = "The analysis engine to use: MSBuild (default), RoslynWorkspace (uses Roslyn, more compatible), or StaticGraph (passes input file directly to MSBuild Static Graph API)",
            DefaultValueFactory = _ => AnalysisEngine.MSBuild,
        };

        var traversalBeforeImportOption = new Option<string?>("--traversal-before-import")
        {
            Description = "Path of the import added before the ProjectReference items in the generated Traversal SDK file (default: <output-name>.before.proj)",
        };

        var traversalSdkVersionOption = new Option<string?>("--traversal-sdk-version")
        {
            Description = "Optional version appended to the Traversal SDK in generated Traversal files (for example: Microsoft.Build.Traversal/4.1.82). When omitted, no version suffix is added.",
        };

        var traversalAfterImportOption = new Option<string?>("--traversal-after-import")
        {
            Description = "Path of the import added after the ProjectReference items in the generated Traversal SDK file (default: <output-name>.after.proj)",
        };

        var generateCommand = new Command("generate", "Generate a subset solution/build file for incremental CI builds")
        {
            inputOption,
            outputOption,
            repositoryOption,
            headCommitOption,
            baseCommitOption,
            baseBranchOption,
            workingTreeOption,
            includeOption,
            fullRebuildTriggerOption,
            hierarchicalRebuildTriggerOption,
            engineOption,
            traversalBeforeImportOption,
            traversalSdkVersionOption,
            traversalAfterImportOption,
        };

        generateCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new DeltaBuildOptions
            {
                InputPath = parseResult.GetValue(inputOption)!,
                OutputPath = parseResult.GetValue(outputOption)!,
                RepositoryPath = parseResult.GetValue(repositoryOption)!,
                HeadCommit = parseResult.GetValue(headCommitOption),
                BaseCommit = parseResult.GetValue(baseCommitOption),
                BaseBranch = parseResult.GetValue(baseBranchOption),
                CompareWorkingTree = parseResult.GetValue(workingTreeOption),
                IncludePatterns = parseResult.GetValue(includeOption) ?? [],
                FullRebuildTriggerPatterns = parseResult.GetValue(fullRebuildTriggerOption) ?? [],
                HierarchicalRebuildTriggerPatterns = parseResult.GetValue(hierarchicalRebuildTriggerOption) ?? [],
                Engine = parseResult.GetValue(engineOption),
                TraversalBeforeImport = parseResult.GetValue(traversalBeforeImportOption),
                TraversalSdkVersion = parseResult.GetValue(traversalSdkVersionOption),
                TraversalAfterImport = parseResult.GetValue(traversalAfterImportOption),
            };

            return await DeltaBuildEngine.RunAsync(options, Console.Out, cancellationToken);
        });

        var rootCommand = new RootCommand("Delta Build — incremental CI build tool")
        {
            generateCommand,
        };

        var configuration = rootCommand.Parse(args);
        return configuration.Invoke();
    }
}
