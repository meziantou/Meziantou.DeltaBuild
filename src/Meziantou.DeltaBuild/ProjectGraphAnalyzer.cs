using Meziantou.Framework;
using Microsoft.Build.Graph;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Uses the MSBuild Static Graph API to build the project dependency graph,
/// extract file ownership for each project, and provide reverse dependency information.
/// Must be called AFTER MSBuildLocator.RegisterDefaults() and in a separate class
/// to avoid loading Microsoft.Build assemblies too early.
/// </summary>
internal static class ProjectGraphAnalyzer
{
    /// <param name="changedFiles">The normalized paths of the changed files. Only these files are recorded in <see cref="ProjectInfo.OwnedFiles"/>.</param>
    public static Dictionary<string, ProjectInfo> Analyze(IReadOnlyList<FullPath> projectPaths, IReadOnlySet<string> changedFiles, string? whereCondition, TextWriter? log = null)
    {
        log?.WriteLine($"Building project graph for {projectPaths.Count} entry point(s)...");

        var entryPoints = projectPaths
            .Select(p => new ProjectGraphEntryPoint(p));

        var graph = new ProjectGraph(entryPoints);

        log?.WriteLine($"Graph built in {graph.ConstructionMetrics.ConstructionTime.TotalMilliseconds:F0}ms: {graph.ConstructionMetrics.NodeCount} nodes, {graph.ConstructionMetrics.EdgeCount} edges");

        // Deduplicate nodes by project path (multi-targeting produces multiple nodes for the same project)
        var nodesByPath = new Dictionary<string, List<ProjectGraphNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.ProjectNodes)
        {
            var normalizedPath = NormalizePath(node.ProjectInstance.FullPath);
            if (!nodesByPath.TryGetValue(normalizedPath, out var nodes))
            {
                nodes = [];
                nodesByPath[normalizedPath] = nodes;
            }
            nodes.Add(node);
        }

        // Build the result dictionary
        var result = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (projectPath, nodes) in nodesByPath)
        {
            var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isTestProject = false;
            var matchesWhereCondition = whereCondition is null;

            foreach (var node in nodes)
            {
                if (!isTestProject)
                {
                    var propertyValue = node.ProjectInstance.GetPropertyValue("IsTestProject");
                    isTestProject = ProjectInfo.IsTruePropertyValue(propertyValue);
                }

                if (whereCondition is not null && !matchesWhereCondition)
                {
                    matchesWhereCondition = ProjectCondition.Evaluate(node.ProjectInstance, whereCondition);
                }

                // Extract file items from ProjectInstance
                try
                {
                    var fileItemTypes = DeltaBuildItems.GetItemTypes(node.ProjectInstance, DeltaBuildItems.DefaultItemTypes);
                    DeltaBuildItems.AddItemFiles(node.ProjectInstance, fileItemTypes, changedFiles, ownedFiles);
                    DeltaBuildItems.AddIncludedFiles(node.ProjectInstance, changedFiles, ownedFiles);
                    DeltaBuildItems.AddExcludedFiles(node.ProjectInstance, changedFiles, excludedFiles);
                }
                catch (Exception ex)
                {
                    log?.WriteLine($"Warning: Failed to extract file items from {projectPath}: {ex.Message}");
                }

                // Also include the project file itself
                if (changedFiles.Contains(projectPath))
                {
                    ownedFiles.Add(projectPath);
                }

                // Include imported files (.props, .targets, etc.)
                foreach (var importPath in node.ProjectInstance.ImportPaths)
                {
                    AddIfChanged(importPath, changedFiles, ownedFiles);
                }

                // Collect forward dependencies
                foreach (var dep in node.ProjectReferences)
                {
                    referencedProjectPaths.Add(NormalizePath(dep.ProjectInstance.FullPath));
                }

                // Collect reverse dependencies
                foreach (var dependent in node.ReferencingProjects)
                {
                    referencingProjectPaths.Add(NormalizePath(dependent.ProjectInstance.FullPath));
                }
            }

            // Remove the files excluded by DeltaBuildExcludeFile
            ownedFiles.ExceptWith(excludedFiles);

            result[projectPath] = new ProjectInfo
            {
                ProjectPath = projectPath,
                OwnedFiles = ownedFiles,
                ReferencedProjectPaths = referencedProjectPaths,
                ReferencingProjectPaths = referencingProjectPaths,
                IsTestProject = isTestProject,
                MatchesWhereCondition = matchesWhereCondition,
            };
        }

        return result;
    }

    /// <summary>
    /// Given a set of directly affected project paths, find all transitively dependent projects
    /// by walking up the ReferencingProjects edges.
    /// </summary>
    public static HashSet<string> GetTransitiveDependents(
        IEnumerable<string> directlyAffectedPaths,
        Dictionary<string, ProjectInfo> projectInfos)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var path in directlyAffectedPaths)
        {
            if (affected.Add(path))
            {
                queue.Enqueue(path);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!projectInfos.TryGetValue(current, out var info))
                continue;

            foreach (var dependent in info.ReferencingProjectPaths)
            {
                if (affected.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Adds the normalized path to <paramref name="files"/> when it is one of the changed files.
    /// The owned files of a project are only used to find the projects that own a changed file,
    /// so this avoids building large sets containing all the items and imports of every project.
    /// </summary>
    internal static void AddIfChanged(string path, IReadOnlySet<string> changedFiles, HashSet<string> files)
    {
        var normalizedPath = NormalizePath(path);
        if (changedFiles.Contains(normalizedPath))
        {
            files.Add(normalizedPath);
        }
    }

    internal static string NormalizePath(string path)
    {
        return FullPath.FromPath(path).Value.Replace('\\', '/');
    }

    internal static string NormalizePath(FullPath path)
    {
        return path.Value.Replace('\\', '/');
    }
}
