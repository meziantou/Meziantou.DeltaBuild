using Meziantou.Framework;
using Meziantou.Framework.Globbing;

namespace Meziantou.DeltaBuild;

internal static class DeltaBuildEngine
{
    private static readonly string[] DefaultFullRebuildTriggerPatterns =
    [
        "**/global.json",
        "**/nuget.config",
        "**/.editorconfig",
    ];

    public static async Task<int> RunAsync(DeltaBuildOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        var repositoryPath = FullPath.FromPath(options.RepositoryPath);

        // Step 1: Resolve commits
        var headCommit = options.HeadCommit;
        if (string.IsNullOrEmpty(headCommit))
        {
            log.WriteLine("No --head-commit provided, using HEAD");
            headCommit = await GitHelper.GetHeadCommitAsync(repositoryPath, cancellationToken);
        }

        var baseCommit = options.BaseCommit;
        if (string.IsNullOrEmpty(baseCommit))
        {
            log.WriteLine($"No --base-commit provided, computing merge-base with {options.BaseBranch}");
            baseCommit = await GitHelper.GetMergeBaseAsync(repositoryPath, headCommit, options.BaseBranch, cancellationToken);
        }

        log.WriteLine($"Comparing {baseCommit} -> {headCommit}");

        // Step 2: Get changed files
        var changedFiles = await GitHelper.GetChangedFilesAsync(repositoryPath, baseCommit, headCommit, cancellationToken);
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

        // Step 6: Check full-rebuild triggers
        var triggerPatterns = options.FullRebuildTriggerPatterns.Length > 0
            ? options.FullRebuildTriggerPatterns
            : DefaultFullRebuildTriggerPatterns;

        if (IsFullRebuildTriggered(changedFiles, triggerPatterns))
        {
            log.WriteLine("Full rebuild triggered: a changed file matches a full-rebuild-trigger pattern.");
            await OutputWriter.WriteAsync(options, input, projectPaths, log, cancellationToken);
            return 0;
        }

        // Step 7: Build project graph and analyze
        var projectInfos = ProjectGraphAnalyzer.Analyze(projectPaths, log);

        // Step 8: Determine directly affected projects
        var normalizedChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var absolutePath = FullPath.Combine(repositoryPath, file);
            normalizedChangedFiles.Add(ProjectGraphAnalyzer.NormalizePath(absolutePath));
        }

        var directlyAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
}
