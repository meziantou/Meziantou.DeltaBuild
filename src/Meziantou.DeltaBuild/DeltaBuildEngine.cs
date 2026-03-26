using Meziantou.Framework;
using Meziantou.Framework.Globbing;

namespace Meziantou.DeltaBuild;

internal static class DeltaBuildEngine
{
    private static readonly string[] DefaultFullRebuildTriggerPatterns = [];

    private static readonly string[] DefaultHierarchicalRebuildTriggerPatterns =
    [
        "**/global.json",
        "**/nuget.config",
        "**/NuGet.config",
        "**/NuGet.Config",
        "**/.editorconfig",
    ];

    public static async Task<int> RunAsync(DeltaBuildOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        var repositoryPath = FullPath.FromPath(options.RepositoryPath);

        var isWorkingTree = options.CompareWorkingTree;

        // Step 1: Resolve commits
        string? headCommit;
        if (isWorkingTree)
        {
            log.WriteLine("Using working tree as head (staged + unstaged + untracked files)");
            headCommit = null;
        }
        else
        {
            headCommit = options.HeadCommit;
            if (string.IsNullOrEmpty(headCommit))
            {
                log.WriteLine("No --head-commit provided, using HEAD");
                headCommit = await GitHelper.GetHeadCommitAsync(repositoryPath, cancellationToken);
            }
        }

        var baseCommit = options.BaseCommit;
        if (string.IsNullOrEmpty(baseCommit))
        {
            var baseBranch = options.BaseBranch;
            if (string.IsNullOrEmpty(baseBranch))
            {
                baseBranch = await GitHelper.GetDefaultBranchAsync(repositoryPath, cancellationToken);
                log.WriteLine($"Auto-detected base branch: {baseBranch}");
            }

            var mergeBaseRef = headCommit ?? "HEAD";
            log.WriteLine($"No --base-commit provided, computing merge-base with {baseBranch}");
            baseCommit = await GitHelper.GetMergeBaseAsync(repositoryPath, mergeBaseRef, baseBranch, cancellationToken);
        }

        log.WriteLine($"Comparing {baseCommit} -> {(isWorkingTree ? "working-tree" : headCommit)}");

        // Step 2: Get changed files
        IReadOnlyList<string> changedFiles;
        if (isWorkingTree)
        {
            changedFiles = await GitHelper.GetWorkingTreeChangedFilesAsync(repositoryPath, baseCommit, cancellationToken);
        }
        else
        {
            changedFiles = await GitHelper.GetChangedFilesAsync(repositoryPath, baseCommit, headCommit!, cancellationToken);
        }

        log.WriteLine($"Found {changedFiles.Count} changed file(s)");

        foreach (var file in changedFiles)
        {
            log.WriteLine($"  Changed: {file}");
        }

        // Step 3: Parse the input file
        var input = await InputReader.ReadAsync(options.InputPath, cancellationToken);
        log.WriteLine($"Input format: {input.Format}, {input.ProjectAbsolutePaths.Count} project(s)");

        // Step 4: Filter projects by --include globs if provided
        var projectPaths = input.ProjectAbsolutePaths;
        if (options.IncludePatterns.Length > 0)
        {
            projectPaths = FilterProjectsByGlobs(projectPaths, options.IncludePatterns, repositoryPath);
            log.WriteLine($"After --include filter: {projectPaths.Count} project(s)");
        }

        if (projectPaths.Count == 0)
        {
            log.WriteLine("No projects to analyze after filtering.");
            await OutputWriter.WriteAsync(options, input, [], log, cancellationToken);
            return 0;
        }

        // Step 5: Skip projects whose files don't exist on disk
        var missingProjects = projectPaths.Where(p => !File.Exists(p)).ToList();
        if (missingProjects.Count > 0)
        {
            foreach (var missing in missingProjects)
            {
                log.WriteLine($"  Warning: Project file not found, skipping: {missing}");
            }

            projectPaths = projectPaths.Where(p => File.Exists(p)).ToList();
            log.WriteLine($"After skipping missing projects: {projectPaths.Count} project(s)");

            if (projectPaths.Count == 0)
            {
                log.WriteLine("No projects to analyze after removing missing projects.");
                await OutputWriter.WriteAsync(options, input, [], log, cancellationToken);
                return 0;
            }
        }

        // Step 6: Check full-rebuild triggers (affects ALL projects)
        var fullTriggerPatterns = options.FullRebuildTriggerPatterns.Length > 0
            ? options.FullRebuildTriggerPatterns
            : DefaultFullRebuildTriggerPatterns;

        if (IsFullRebuildTriggered(changedFiles, fullTriggerPatterns))
        {
            log.WriteLine("Full rebuild triggered: a changed file matches a full-rebuild-trigger pattern.");
            await OutputWriter.WriteAsync(options, input, projectPaths, log, cancellationToken);
            return 0;
        }

        // Step 6b: Check hierarchical-rebuild triggers (affects only projects in the same folder hierarchy)
        var hierarchicalTriggerPatterns = options.HierarchicalRebuildTriggerPatterns.Length > 0
            ? options.HierarchicalRebuildTriggerPatterns
            : DefaultHierarchicalRebuildTriggerPatterns;

        var hierarchicallyAffectedProjects = GetHierarchicallyAffectedProjects(
            changedFiles, hierarchicalTriggerPatterns, projectPaths, repositoryPath, log);

        // Step 7: Build project graph and analyze
        log.WriteLine($"Analyzing projects using engine: {options.Engine}");
        var projectInfos = options.Engine switch
        {
            AnalysisEngine.StaticGraph => StaticGraphProjectAnalyzer.Analyze(input.InputFilePath, log),
            AnalysisEngine.RoslynWorkspace => await WorkspaceProjectAnalyzer.AnalyzeAsync(projectPaths, log, cancellationToken),
            _ => ProjectGraphAnalyzer.Analyze(projectPaths, log),
        };

        // Step 8: Determine directly affected projects
        var normalizedChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var absolutePath = FullPath.Combine(repositoryPath, file);
            normalizedChangedFiles.Add(ProjectGraphAnalyzer.NormalizePath(absolutePath));
        }

        var directlyAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include projects affected by hierarchical triggers
        foreach (var project in hierarchicallyAffectedProjects)
        {
            directlyAffected.Add(ProjectGraphAnalyzer.NormalizePath(project));
        }

        foreach (var (projectPath, info) in projectInfos)
        {
            foreach (var ownedFile in info.OwnedFiles)
            {
                if (normalizedChangedFiles.Contains(ownedFile))
                {
                    directlyAffected.Add(projectPath);
                    log.WriteLine($"  Directly affected: {projectPath} (due to {ownedFile})");
                    break;
                }
            }
        }

        log.WriteLine($"Directly affected: {directlyAffected.Count} project(s)");

        // Step 9: Find transitive dependents
        var allAffected = ProjectGraphAnalyzer.GetTransitiveDependents(directlyAffected, projectInfos);

        // For SingleProject input, include all projects discovered by the graph as candidates.
        // For other formats (Traversal, SLN, SLNX), only the explicitly listed input projects are candidates.
        HashSet<string> inputProjectSet;
        if (input.Format == InputFormat.SingleProject)
        {
            inputProjectSet = new HashSet<string>(projectInfos.Keys, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            inputProjectSet = new HashSet<string>(
                projectPaths.Select(p => ProjectGraphAnalyzer.NormalizePath(p)),
                StringComparer.OrdinalIgnoreCase);
        }

        var affectedInputProjects = allAffected
            .Where(p => inputProjectSet.Contains(p))
            .Select(FullPath.FromPath)
            .ToList();

        log.WriteLine($"Total affected (including transitive dependents): {affectedInputProjects.Count} project(s)");

        foreach (var project in affectedInputProjects)
        {
            log.WriteLine($"  Affected: {project}");
        }

        // Step 10: Write output
        await OutputWriter.WriteAsync(options, input, affectedInputProjects, log, cancellationToken);

        return 0;
    }

    private static List<FullPath> FilterProjectsByGlobs(
        IReadOnlyList<FullPath> projectPaths,
        string[] includePatterns,
        FullPath repositoryPath)
    {
        var globs = includePatterns
            .Select(p => Glob.Parse(p, GlobOptions.None))
            .ToArray();

        var filtered = new List<FullPath>();

        foreach (var projectPath in projectPaths)
        {
            var relativePath = projectPath.MakePathRelativeTo(repositoryPath).Replace('\\', '/');

            foreach (var glob in globs)
            {
                if (glob.IsMatch(relativePath))
                {
                    filtered.Add(projectPath);
                    break;
                }
            }
        }

        return filtered;
    }

    private static bool IsFullRebuildTriggered(IReadOnlyList<string> changedFiles, string[] triggerPatterns)
    {
        if (triggerPatterns.Length == 0)
            return false;

        var globs = triggerPatterns
            .Select(p => Glob.Parse(p, GlobOptions.None))
            .ToArray();

        foreach (var file in changedFiles)
        {
            // Changed files from git are already relative to repo root
            var normalizedFile = file.Replace('\\', '/');

            foreach (var glob in globs)
            {
                if (glob.IsMatch(normalizedFile))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// For each changed file matching a hierarchical trigger pattern, find projects whose directory
    /// is the same as or below the changed file's directory. For example, if "src/global.json" changes,
    /// projects under "src/" are affected but projects under "tests/" are not.
    /// </summary>
    private static HashSet<FullPath> GetHierarchicallyAffectedProjects(
        IReadOnlyList<string> changedFiles,
        string[] triggerPatterns,
        IReadOnlyList<FullPath> projectPaths,
        FullPath repositoryPath,
        TextWriter log)
    {
        var affected = new HashSet<FullPath>();

        if (triggerPatterns.Length == 0)
            return affected;

        var globs = triggerPatterns
            .Select(p => Glob.Parse(p, GlobOptions.None))
            .ToArray();

        // Find changed files that match hierarchical trigger patterns
        var triggerFiles = new List<string>();
        foreach (var file in changedFiles)
        {
            var normalizedFile = file.Replace('\\', '/');
            foreach (var glob in globs)
            {
                if (glob.IsMatch(normalizedFile))
                {
                    triggerFiles.Add(normalizedFile);
                    break;
                }
            }
        }

        if (triggerFiles.Count == 0)
            return affected;

        // For each trigger file, compute its directory (relative to repo root) and find projects under it
        foreach (var triggerFile in triggerFiles)
        {
            // Get directory of the trigger file relative to the repo root
            // e.g., "src/global.json" -> "src/", "global.json" -> ""
            var lastSlash = triggerFile.LastIndexOf('/');
            var triggerDir = lastSlash >= 0 ? triggerFile[..(lastSlash + 1)] : "";

            foreach (var projectPath in projectPaths)
            {
                if (affected.Contains(projectPath))
                    continue;

                var projectRelative = projectPath.MakePathRelativeTo(repositoryPath).Replace('\\', '/');

                // A project is affected if it is at or below the trigger file's directory
                // e.g., trigger dir "src/" affects "src/proj1/proj1.csproj" but not "tests/proj1.tests/proj1.tests.csproj"
                // An empty trigger dir (root-level file) affects all projects
                if (triggerDir.Length == 0 || projectRelative.StartsWith(triggerDir, StringComparison.OrdinalIgnoreCase))
                {
                    affected.Add(projectPath);
                    log.WriteLine($"  Hierarchical trigger: {projectPath} (due to {triggerFile})");
                }
            }
        }

        return affected;
    }
}
