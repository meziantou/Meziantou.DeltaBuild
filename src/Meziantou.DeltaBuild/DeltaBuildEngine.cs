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
        ValidateShardOptions(options);

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
                baseBranch = TryGetGitHubActionsPullRequestBaseBranch();
                if (string.IsNullOrEmpty(baseBranch))
                {
                    baseBranch = await GitHelper.GetDefaultBranchAsync(repositoryPath, cancellationToken);
                    log.WriteLine($"Auto-detected base branch: {baseBranch}");
                }
                else
                {
                    log.WriteLine($"Detected GitHub Actions pull request context, using base branch: {baseBranch}");
                }
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
        var existingProjects = new List<FullPath>(projectPaths.Count);
        var missingProjects = new List<FullPath>();
        foreach (var projectPath in projectPaths)
        {
            if (File.Exists(projectPath))
            {
                existingProjects.Add(projectPath);
            }
            else
            {
                missingProjects.Add(projectPath);
            }
        }

        if (missingProjects.Count > 0)
        {
            foreach (var missing in missingProjects)
            {
                log.WriteLine($"  Warning: Project file not found, skipping: {missing}");
            }

            projectPaths = existingProjects;
            log.WriteLine($"After skipping missing projects: {projectPaths.Count} project(s)");

            if (projectPaths.Count == 0)
            {
                log.WriteLine("No projects to analyze after removing missing projects.");
                await OutputWriter.WriteAsync(options, input, [], log, cancellationToken);
                return 0;
            }
        }

        if (changedFiles.Count == 0)
        {
            log.WriteLine("No changed files detected. Skipping project graph analysis.");
            await OutputWriter.WriteAsync(options, input, [], log, cancellationToken);
            return 0;
        }

        // Step 6: Check full-rebuild triggers (affects ALL projects)
        var fullTriggerPatterns = options.FullRebuildTriggerPatterns.Length > 0
            ? options.FullRebuildTriggerPatterns
            : DefaultFullRebuildTriggerPatterns;

        var fullRebuildTriggerFiles = GetMatchingTriggerFiles(changedFiles, fullTriggerPatterns);
        var fullRebuildTriggerFile = fullRebuildTriggerFiles.Count > 0 ? fullRebuildTriggerFiles[0] : null;
        if (fullRebuildTriggerFile is not null)
        {
            log.WriteLine($"Full rebuild triggered by global file '{fullRebuildTriggerFile}'.");

            if (!options.TestProjectsOnly)
            {
                foreach (var projectPath in projectPaths.OrderBy(p => p, FullPathComparer.Default))
                {
                    var projectDisplayPath = ToRepositoryRelativePath(projectPath, repositoryPath);
                    log.WriteLine($"  Adding project '{projectDisplayPath}' because global file '{fullRebuildTriggerFile}' changed.");
                }

                await OutputWriter.WriteAsync(options, input, projectPaths, log, cancellationToken);
                return 0;
            }
        }

        // Step 6b: Check hierarchical-rebuild triggers (affects only projects in the same folder hierarchy)
        var hierarchicalTriggerPatterns = options.HierarchicalRebuildTriggerPatterns.Length > 0
            ? options.HierarchicalRebuildTriggerPatterns
            : DefaultHierarchicalRebuildTriggerPatterns;

        var hierarchicallyAffectedProjects = GetHierarchicallyAffectedProjects(
            changedFiles, hierarchicalTriggerPatterns, projectPaths, repositoryPath);

        // Step 7: Build project graph and analyze
        log.WriteLine($"Analyzing projects using engine: {options.Engine}");
        var projectInfos = options.Engine switch
        {
            AnalysisEngine.StaticGraph => StaticGraphProjectAnalyzer.Analyze(input.InputFilePath, log),
            AnalysisEngine.RoslynWorkspace => await WorkspaceProjectAnalyzer.AnalyzeAsync(projectPaths, log, cancellationToken),
            _ => ProjectGraphAnalyzer.Analyze(projectPaths, log),
        };

        if (fullRebuildTriggerFile is not null)
        {
            var fullRebuildAffectedProjects = projectPaths
                .OrderBy(p => p, FullPathComparer.Default)
                .ToList();

            fullRebuildAffectedProjects = FilterToTestProjects(fullRebuildAffectedProjects, projectInfos, log);

            foreach (var projectPath in fullRebuildAffectedProjects)
            {
                var projectDisplayPath = ToRepositoryRelativePath(projectPath, repositoryPath);
                log.WriteLine($"  Adding project '{projectDisplayPath}' because global file '{fullRebuildTriggerFile}' changed.");
            }

            await OutputWriter.WriteAsync(options, input, fullRebuildAffectedProjects, log, cancellationToken);
            return 0;
        }

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

        var (bundleMembersByProjectPath, bundleCount) = ParseProjectBundles(options.ProjectBundles, inputProjectSet, repositoryPath);
        if (bundleCount > 0)
        {
            log.WriteLine($"Configured {bundleCount} project bundle(s).");
        }

        // Step 8: Determine directly affected projects
        var normalizedChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedFilePathByNormalizedPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var normalizedFile = file.Replace('\\', '/');
            var absolutePath = FullPath.Combine(repositoryPath, normalizedFile);
            var normalizedAbsolutePath = ProjectGraphAnalyzer.NormalizePath(absolutePath);

            normalizedChangedFiles.Add(normalizedAbsolutePath);
            changedFilePathByNormalizedPath.TryAdd(normalizedAbsolutePath, normalizedFile);
        }

        var owningProjectsByChangedOwnedFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (projectPath, info) in projectInfos)
        {
            foreach (var ownedFile in info.OwnedFiles)
            {
                if (!normalizedChangedFiles.Contains(ownedFile))
                    continue;

                if (!owningProjectsByChangedOwnedFile.TryGetValue(ownedFile, out var owningProjects))
                {
                    owningProjects = [];
                    owningProjectsByChangedOwnedFile[ownedFile] = owningProjects;
                }

                owningProjects.Add(projectPath);
            }
        }

        var directlyAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasonByProjectPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Include projects affected by hierarchical triggers
        foreach (var (projectPath, triggerFile) in hierarchicallyAffectedProjects)
        {
            directlyAffected.Add(projectPath);
            reasonByProjectPath.TryAdd(projectPath, $"hierarchical file '{triggerFile}' changed");
        }

        foreach (var (ownedFile, owningProjects) in owningProjectsByChangedOwnedFile)
        {
            var changedFilePath = changedFilePathByNormalizedPath.TryGetValue(ownedFile, out var filePath)
                ? filePath
                : ToRepositoryRelativePath(FullPath.FromPath(ownedFile), repositoryPath);

            var reason = owningProjects.Count > 1
                ? $"global file '{changedFilePath}' changed"
                : $"file '{changedFilePath}' changed";

            foreach (var projectPath in owningProjects)
            {
                directlyAffected.Add(projectPath);

                if (reasonByProjectPath.TryGetValue(projectPath, out var existingReason))
                {
                    if (existingReason.StartsWith("hierarchical file", StringComparison.Ordinal))
                    {
                        reasonByProjectPath[projectPath] = reason;
                    }
                }
                else
                {
                    reasonByProjectPath[projectPath] = reason;
                }
            }
        }

        log.WriteLine($"Directly affected: {directlyAffected.Count} project(s)");

        // Step 9: Expand affected projects using bundles and transitive dependents, and track reasons
        var allAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var path in directlyAffected)
        {
            if (allAffected.Add(path))
            {
                queue.Enqueue(path);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDisplayPath = ToRepositoryRelativePath(FullPath.FromPath(current), repositoryPath);

            if (bundleMembersByProjectPath.TryGetValue(current, out var bundledProjects))
            {
                foreach (var bundledProject in bundledProjects)
                {
                    if (!allAffected.Add(bundledProject))
                        continue;

                    queue.Enqueue(bundledProject);

                    if (!reasonByProjectPath.ContainsKey(bundledProject))
                    {
                        reasonByProjectPath[bundledProject] = $"it is bundled with affected project '{currentDisplayPath}'";
                    }
                }
            }

            if (!projectInfos.TryGetValue(current, out var info))
                continue;

            foreach (var dependent in info.ReferencingProjectPaths)
            {
                if (!allAffected.Add(dependent))
                    continue;

                queue.Enqueue(dependent);

                if (!reasonByProjectPath.ContainsKey(dependent))
                {
                    reasonByProjectPath[dependent] = $"it depends on affected project '{currentDisplayPath}'";
                }
            }
        }

        var affectedInputProjects = allAffected
            .Where(p => inputProjectSet.Contains(p))
            .Select(FullPath.FromPath)
            .OrderBy(p => p, FullPathComparer.Default)
            .ToList();

        log.WriteLine($"Total affected (including transitive dependents): {affectedInputProjects.Count} project(s)");

        if (options.TestProjectsOnly)
        {
            affectedInputProjects = FilterToTestProjects(affectedInputProjects, projectInfos, log);
        }

        foreach (var project in affectedInputProjects)
        {
            var normalizedProjectPath = ProjectGraphAnalyzer.NormalizePath(project);
            var projectDisplayPath = ToRepositoryRelativePath(project, repositoryPath);

            if (reasonByProjectPath.TryGetValue(normalizedProjectPath, out var reason))
            {
                log.WriteLine($"  Adding project '{projectDisplayPath}' because {reason}.");
            }
            else
            {
                log.WriteLine($"  Adding project '{projectDisplayPath}' because it is affected.");
            }
        }

        // Step 10: Write output
        await OutputWriter.WriteAsync(options, input, affectedInputProjects, log, cancellationToken);

        return 0;
    }

    private static void ValidateShardOptions(DeltaBuildOptions options)
    {
        if (options.Shard is { } shard && shard <= 0)
        {
            throw new InvalidOperationException("The --shard value must be greater than 0.");
        }

        if (options.TotalShards is { } totalShards && totalShards <= 0)
        {
            throw new InvalidOperationException("The --total-shards value must be greater than 0.");
        }

        if (options.Shard is null && options.TotalShards is not null)
        {
            throw new InvalidOperationException("The --total-shards option must be used with --shard.");
        }

        if (options.Shard is not null && options.TotalShards is null)
        {
            throw new InvalidOperationException("The --shard option must be used with --total-shards.");
        }

        if (options.Shard is { } currentShard &&
            options.TotalShards is { } configuredTotalShards &&
            currentShard > configuredTotalShards)
        {
            throw new InvalidOperationException("The --shard value must be less than or equal to --total-shards.");
        }
    }

    private static List<FullPath> FilterToTestProjects(
        List<FullPath> projectPaths,
        Dictionary<string, ProjectInfo> projectInfos,
        TextWriter log)
    {
        var result = new List<FullPath>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            var normalizedProjectPath = ProjectGraphAnalyzer.NormalizePath(projectPath);
            if (projectInfos.TryGetValue(normalizedProjectPath, out var projectInfo) &&
                projectInfo.IsTestProject)
            {
                result.Add(projectPath);
            }
        }

        log.WriteLine($"After --test-projects-only filter: {result.Count} project(s)");
        return result;
    }

    private static string? TryGetGitHubActionsPullRequestBaseBranch()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
        var isPullRequestEvent = string.Equals(eventName, "pull_request", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(eventName, "pull_request_target", StringComparison.OrdinalIgnoreCase);
        if (!isPullRequestEvent)
            return null;

        var baseRef = Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
        if (string.IsNullOrWhiteSpace(baseRef))
            return null;

        const string HeadsPrefix = "refs/heads/";
        if (baseRef.StartsWith(HeadsPrefix, StringComparison.Ordinal))
        {
            baseRef = baseRef[HeadsPrefix.Length..];
        }

        return $"origin/{baseRef}";
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

    private static (Dictionary<string, HashSet<string>> MembersByProjectPath, int BundleCount) ParseProjectBundles(
        string[] bundleDefinitions,
        HashSet<string> inputProjectSet,
        FullPath repositoryPath)
    {
        var membersByProjectPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var bundleCount = 0;

        foreach (var definition in bundleDefinitions)
        {
            var members = definition
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(member => NormalizeBundleProjectPath(member, repositoryPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (members.Length == 0)
            {
                throw new InvalidOperationException($"Project bundle '{definition}' does not contain any project path.");
            }

            var invalidMembers = members
                .Where(member => !inputProjectSet.Contains(member))
                .Select(member => ToRepositoryRelativePath(FullPath.FromPath(member), repositoryPath))
                .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (invalidMembers.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Project bundle '{definition}' references project(s) not present in the input project set: {string.Join(", ", invalidMembers)}.");
            }

            foreach (var member in members)
            {
                if (!membersByProjectPath.TryGetValue(member, out var bundledProjects))
                {
                    bundledProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    membersByProjectPath[member] = bundledProjects;
                }

                foreach (var otherMember in members)
                {
                    if (!string.Equals(member, otherMember, StringComparison.OrdinalIgnoreCase))
                    {
                        bundledProjects.Add(otherMember);
                    }
                }
            }

            bundleCount++;
        }

        return (membersByProjectPath, bundleCount);
    }

    private static string NormalizeBundleProjectPath(string path, FullPath repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Project bundle contains an empty project path.");
        }

        FullPath absolutePath;
        if (Path.IsPathRooted(path))
        {
            absolutePath = FullPath.FromPath(path);
        }
        else
        {
            var normalizedPath = path.Replace('\\', '/');
            absolutePath = FullPath.Combine(repositoryPath, normalizedPath);
        }

        return ProjectGraphAnalyzer.NormalizePath(absolutePath);
    }

    private static List<string> GetMatchingTriggerFiles(IReadOnlyList<string> changedFiles, string[] triggerPatterns)
    {
        var matchingFiles = new List<string>();

        if (triggerPatterns.Length == 0)
            return matchingFiles;

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
                    matchingFiles.Add(normalizedFile);
                    break;
                }
            }
        }

        return matchingFiles;
    }

    /// <summary>
    /// For each changed file matching a hierarchical trigger pattern, find projects whose directory
    /// is the same as or below the changed file's directory. For example, if "src/global.json" changes,
    /// projects under "src/" are affected but projects under "tests/" are not.
    /// </summary>
    private static Dictionary<string, string> GetHierarchicallyAffectedProjects(
        IReadOnlyList<string> changedFiles,
        string[] triggerPatterns,
        IReadOnlyList<FullPath> projectPaths,
        FullPath repositoryPath)
    {
        var affected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                var normalizedProjectPath = ProjectGraphAnalyzer.NormalizePath(projectPath);
                if (affected.ContainsKey(normalizedProjectPath))
                    continue;

                var projectRelative = projectPath.MakePathRelativeTo(repositoryPath).Replace('\\', '/');

                // A project is affected if it is at or below the trigger file's directory
                // e.g., trigger dir "src/" affects "src/proj1/proj1.csproj" but not "tests/proj1.tests/proj1.tests.csproj"
                // An empty trigger dir (root-level file) affects all projects
                if (triggerDir.Length == 0 || projectRelative.StartsWith(triggerDir, StringComparison.OrdinalIgnoreCase))
                {
                    affected.TryAdd(normalizedProjectPath, triggerFile);
                }
            }
        }

        return affected;
    }

    private static string ToRepositoryRelativePath(FullPath path, FullPath repositoryPath)
    {
        return path.MakePathRelativeTo(repositoryPath).Replace('\\', '/');
    }
}
