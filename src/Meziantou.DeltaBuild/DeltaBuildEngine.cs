using System.Text.Json;
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

        if (options.WhereCondition is not null)
        {
            ProjectCondition.Validate(options.WhereCondition);
        }

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

        var headRef = headCommit ?? "HEAD";
        var baseCommit = options.BaseCommit;
        var baseBranch = options.BaseBranch;
        if (string.IsNullOrEmpty(baseBranch))
        {
            baseBranch = TryGetGitHubActionsPullRequestBaseBranch();
            if (string.IsNullOrEmpty(baseBranch))
            {
                if (string.IsNullOrEmpty(baseCommit))
                {
                    baseBranch = await GitHelper.GetDefaultBranchAsync(repositoryPath, cancellationToken);
                    log.WriteLine($"Auto-detected base branch: {baseBranch}");
                }
                else
                {
                    // The base branch is only used to refine the provided base commit, so it is optional
                    baseBranch = await GitHelper.TryGetDefaultBranchAsync(repositoryPath, cancellationToken);
                    if (baseBranch is not null)
                    {
                        log.WriteLine($"Auto-detected base branch: {baseBranch}");
                    }
                }
            }
            else
            {
                log.WriteLine($"Detected GitHub Actions pull request context, using base branch: {baseBranch}");
            }
        }

        if (string.IsNullOrEmpty(baseCommit))
        {
            log.WriteLine($"No --base-commit provided, computing merge-base with {baseBranch}");
            baseCommit = await GitHelper.GetMergeBaseAsync(repositoryPath, headRef, baseBranch!, cancellationToken);
        }

        if (!string.IsNullOrEmpty(baseBranch))
        {
            baseCommit = await RefineBaseCommitAsync(repositoryPath, baseCommit, headRef, baseBranch, log, cancellationToken);
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

            if (!options.TestProjectsOnly && options.WhereCondition is null)
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

        var changedPackagesByCentralPackageFile = await GetChangedPackagesByCentralPackageFileAsync(
            changedFilePathByNormalizedPath, repositoryPath, baseCommit, headCommit, log, cancellationToken);

        log.WriteLine($"Analyzing projects using engine: {options.Engine}");
        var projectInfos = options.Engine switch
        {
            AnalysisEngine.StaticGraph => StaticGraphProjectAnalyzer.Analyze(input.InputFilePath, normalizedChangedFiles, options.WhereCondition, log),
            AnalysisEngine.RoslynWorkspace => await WorkspaceProjectAnalyzer.AnalyzeAsync(projectPaths, normalizedChangedFiles, options.WhereCondition, log, cancellationToken),
            _ => ProjectGraphAnalyzer.Analyze(projectPaths, normalizedChangedFiles, options.WhereCondition, log),
        };

        if (fullRebuildTriggerFile is not null)
        {
            var fullRebuildAffectedProjects = projectPaths
                .OrderBy(p => p, FullPathComparer.Default)
                .ToList();

            if (options.TestProjectsOnly)
            {
                fullRebuildAffectedProjects = FilterToTestProjects(fullRebuildAffectedProjects, projectInfos, log);
            }

            if (options.WhereCondition is not null)
            {
                fullRebuildAffectedProjects = FilterToProjectsMatchingWhereCondition(fullRebuildAffectedProjects, projectInfos, log);
            }

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
        var owningProjectsByChangedOwnedFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (projectPath, info) in projectInfos)
        {
            foreach (var ownedFile in info.OwnedFiles)
            {
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

        var transitivePackagesCache = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ownedFile, owningProjects) in owningProjectsByChangedOwnedFile)
        {
            var changedFilePath = changedFilePathByNormalizedPath.TryGetValue(ownedFile, out var filePath)
                ? filePath
                : ToRepositoryRelativePath(FullPath.FromPath(ownedFile), repositoryPath);

            var defaultReason = owningProjects.Count > 1
                ? $"global file '{changedFilePath}' changed"
                : $"file '{changedFilePath}' changed";

            changedPackagesByCentralPackageFile.TryGetValue(ownedFile, out var changedPackages);

            foreach (var projectPath in owningProjects)
            {
                var reason = defaultReason;
                if (changedPackages is not null)
                {
                    // Only the projects using one of the changed packages are affected by the Directory.Packages.props change
                    var packageReason = GetCentralPackageChangeReason(projectInfos[projectPath], changedPackages, ownedFile, changedFilePath, transitivePackagesCache);
                    if (packageReason is null)
                        continue;

                    reason = packageReason;
                }

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

        if (options.WhereCondition is not null)
        {
            affectedInputProjects = FilterToProjectsMatchingWhereCondition(affectedInputProjects, projectInfos, log);
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

    /// <summary>
    /// For each changed <c>Directory.Packages.props</c> file, computes the IDs of the packages whose <c>PackageVersion</c> items changed.
    /// The value is <see langword="null"/> when the change cannot be limited to a set of packages, in which case every project
    /// importing the file is affected.
    /// </summary>
    private static async Task<Dictionary<string, HashSet<string>?>> GetChangedPackagesByCentralPackageFileAsync(
        Dictionary<string, string> changedFilePathByNormalizedPath,
        FullPath repositoryPath,
        string baseCommit,
        string? headCommit,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (normalizedPath, relativePath) in changedFilePathByNormalizedPath)
        {
            if (!CentralPackageVersionsDiff.IsCentralPackageVersionsFile(relativePath))
                continue;

            var baseContent = await GitHelper.TryGetFileContentAsync(repositoryPath, baseCommit, relativePath, cancellationToken);
            string? headContent;
            if (headCommit is null)
            {
                headContent = File.Exists(normalizedPath) ? await File.ReadAllTextAsync(normalizedPath, cancellationToken) : null;
            }
            else
            {
                headContent = await GitHelper.TryGetFileContentAsync(repositoryPath, headCommit, relativePath, cancellationToken);
            }

            var changedPackages = CentralPackageVersionsDiff.GetChangedPackages(baseContent, headContent);
            if (changedPackages is null)
            {
                log.WriteLine($"Cannot determine the packages changed in '{relativePath}', all the projects importing it are affected.");
            }
            else if (changedPackages.Count == 0)
            {
                log.WriteLine($"No package version changed in '{relativePath}'.");
            }
            else
            {
                log.WriteLine($"Package versions changed in '{relativePath}': {string.Join(", ", changedPackages.Order(StringComparer.OrdinalIgnoreCase))}");
            }

            result[normalizedPath] = changedPackages;
        }

        return result;
    }

    /// <summary>
    /// Returns the reason why a project is affected by the change of some <c>PackageVersion</c> items of a <c>Directory.Packages.props</c> file,
    /// or <see langword="null"/> if the project doesn't use any of the changed packages.
    /// </summary>
    private static string? GetCentralPackageChangeReason(
        ProjectInfo projectInfo,
        HashSet<string> changedPackages,
        string centralPackageFilePath,
        string centralPackageFileDisplayPath,
        Dictionary<string, HashSet<string>?> transitivePackagesCache)
    {
        if (projectInfo.PackageReferences is null)
            return $"global file '{centralPackageFileDisplayPath}' changed";

        var directPackage = changedPackages
            .Where(projectInfo.PackageReferences.Contains)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (directPackage is not null)
            return $"package '{directPackage}' changed in '{centralPackageFileDisplayPath}'";

        if (!projectInfo.UsesTransitivePinning)
            return null;

        // With transitive pinning, the PackageVersion items also apply to the transitive dependencies.
        // They are only known from the assets file generated by NuGet restore.
        var transitivePackages = GetTransitivePackages(projectInfo, centralPackageFilePath, transitivePackagesCache);
        if (transitivePackages is null)
            return $"transitive pinning is enabled and '{centralPackageFileDisplayPath}' changed";

        var transitivePackage = changedPackages
            .Where(transitivePackages.Contains)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (transitivePackage is not null)
            return $"transitive package '{transitivePackage}' changed in '{centralPackageFileDisplayPath}'";

        return null;
    }

    /// <summary>
    /// Reads the packages of the project from its NuGet assets file (<c>project.assets.json</c>).
    /// Returns <see langword="null"/> when the file doesn't exist, cannot be read, or is older than the project file or
    /// the <c>Directory.Packages.props</c> file, as it may not reflect the current dependencies.
    /// </summary>
    private static HashSet<string>? GetTransitivePackages(
        ProjectInfo projectInfo,
        string centralPackageFilePath,
        Dictionary<string, HashSet<string>?> cache)
    {
        var assetsFile = projectInfo.ProjectAssetsFile;
        if (assetsFile is null || !File.Exists(assetsFile))
            return null;

        var assetsFileLastWriteTime = File.GetLastWriteTimeUtc(assetsFile);
        if (assetsFileLastWriteTime < File.GetLastWriteTimeUtc(projectInfo.ProjectPath) ||
            assetsFileLastWriteTime < File.GetLastWriteTimeUtc(centralPackageFilePath))
        {
            return null;
        }

        if (cache.TryGetValue(assetsFile, out var cachedPackages))
            return cachedPackages;

        HashSet<string>? packages = null;
        try
        {
            using var stream = File.OpenRead(assetsFile);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("libraries", out var libraries) &&
                libraries.ValueKind is JsonValueKind.Object)
            {
                packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind is not JsonValueKind.Object ||
                        !library.Value.TryGetProperty("type", out var type) ||
                        !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var separatorIndex = library.Name.IndexOf('/', StringComparison.Ordinal);
                    packages.Add(separatorIndex >= 0 ? library.Name[..separatorIndex] : library.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            packages = null;
        }

        cache[assetsFile] = packages;
        return packages;
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

        if (options.ShardSeparateProjects.Length > 0 &&
            (options.Shard is null || options.TotalShards is null))
        {
            throw new InvalidOperationException("The --shard-separate option must be used with --shard and --total-shards.");
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

    private static List<FullPath> FilterToProjectsMatchingWhereCondition(
        List<FullPath> projectPaths,
        Dictionary<string, ProjectInfo> projectInfos,
        TextWriter log)
    {
        var result = new List<FullPath>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            var normalizedProjectPath = ProjectGraphAnalyzer.NormalizePath(projectPath);
            if (projectInfos.TryGetValue(normalizedProjectPath, out var projectInfo) &&
                projectInfo.MatchesWhereCondition)
            {
                result.Add(projectPath);
            }
        }

        log.WriteLine($"After --where filter: {result.Count} project(s)");
        return result;
    }

    /// <summary>
    /// The head may contain commits of the base branch that are newer than the base commit, for instance when the base
    /// branch is merged into the current branch to resolve conflicts, or when the head is the merge commit created by
    /// GitHub for a pull request (<c>refs/pull/N/merge</c>). Diffing from the base commit would report the changes of
    /// these commits too. Use the latest commit of the base branch that is merged into the head instead, so the diff
    /// only contains what the head actually changes compared to the base branch.
    /// </summary>
    private static async Task<string> RefineBaseCommitAsync(FullPath repositoryPath, string baseCommit, string headRef, string baseBranch, TextWriter log, CancellationToken cancellationToken)
    {
        // The base commit can be a tree (e.g. the empty tree to list all files). There is no history to look at.
        if (!await GitHelper.IsCommitAsync(repositoryPath, baseCommit, cancellationToken))
            return baseCommit;

        if (!await GitHelper.IsCommitAsync(repositoryPath, baseBranch, cancellationToken))
        {
            log.WriteLine($"Base branch {baseBranch} not found, using base commit {baseCommit} as is");
            return baseCommit;
        }

        var latestMergedCommit = await GitHelper.GetLatestMergedFirstParentCommitAsync(repositoryPath, baseCommit, headRef, baseBranch, cancellationToken);
        if (latestMergedCommit is null)
            return baseCommit;

        log.WriteLine($"Refined base commit {baseCommit} -> {latestMergedCommit} (latest commit of {baseBranch} merged into {headRef})");
        return latestMergedCommit;
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
            .Select(p => Glob.Parse(p, GlobDialect.Standard))
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
            .Select(p => Glob.Parse(p, GlobDialect.Standard))
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
            .Select(p => Glob.Parse(p, GlobDialect.Standard))
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
            var lastSlash = triggerFile.LastIndexOf("/", StringComparison.Ordinal);
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
