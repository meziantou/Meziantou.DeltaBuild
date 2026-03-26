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
            Description = "The base branch for merge-base detection (default: auto-detected from remote)",
        };

        var includeOption = new Option<string[]>("--include")
        {
            Description = "Glob patterns to filter which projects to consider (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        var fullRebuildTriggerOption = new Option<string[]>("--full-rebuild-trigger")
        {
            Description = "Glob patterns for files that trigger a full rebuild (replaces defaults when provided, repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        var generateCommand = new Command("generate", "Generate a subset solution/build file for incremental CI builds")
        {
            inputOption,
            outputOption,
            repositoryOption,
            headCommitOption,
            baseCommitOption,
            baseBranchOption,
            includeOption,
            fullRebuildTriggerOption,
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
                IncludePatterns = parseResult.GetValue(includeOption) ?? [],
                FullRebuildTriggerPatterns = parseResult.GetValue(fullRebuildTriggerOption) ?? [],
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
