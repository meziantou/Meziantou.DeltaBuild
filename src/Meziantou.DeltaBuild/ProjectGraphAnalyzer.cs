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
    private static readonly HashSet<string> FileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile",
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "TypeScriptCompile",
    };

    public static Dictionary<string, ProjectInfo> Analyze(IReadOnlyList<FullPath> projectPaths, TextWriter? log = null)
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
            var referencedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                // Extract file items from ProjectInstance
                try
                {
                    foreach (var item in node.ProjectInstance.Items)
                    {
                        if (!FileItemTypes.Contains(item.ItemType))
                            continue;

                        var fullPath = item.GetMetadataValue("FullPath");
                        if (!string.IsNullOrEmpty(fullPath))
                        {
                            ownedFiles.Add(NormalizePath(fullPath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.WriteLine($"Warning: Failed to extract file items from {projectPath}: {ex.Message}");
                }

                // Also include the project file itself
                ownedFiles.Add(projectPath);

                // Include imported files (.props, .targets, etc.)
                foreach (var importPath in node.ProjectInstance.ImportPaths)
                {
                    ownedFiles.Add(NormalizePath(importPath));
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

            result[projectPath] = new ProjectInfo
            {
                ProjectPath = projectPath,
                OwnedFiles = ownedFiles,
                ReferencedProjectPaths = referencedProjectPaths,
                ReferencingProjectPaths = referencingProjectPaths,
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

    internal static string NormalizePath(string path)
    {
        return FullPath.FromPath(path).Value.Replace('\\', '/');
    }

    internal static string NormalizePath(FullPath path)
    {
        return path.Value.Replace('\\', '/');
    }
}
