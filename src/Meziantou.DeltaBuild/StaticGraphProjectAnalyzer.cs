using Meziantou.Framework;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Uses the MSBuild Static Graph API with the input file (SLN, SLNX, Traversal, or project)
/// as a single entry point, letting MSBuild handle solution/traversal parsing natively
/// with parallel evaluation. Inspired by Petabridge's Incrementalist StaticGraphBuildEngine.
/// This differs from the MSBuild engine which passes individual project paths as entry points.
/// Must be called AFTER MSBuildLocator.RegisterDefaults() and in a separate class
/// to avoid loading Microsoft.Build assemblies too early.
/// </summary>
internal static class StaticGraphProjectAnalyzer
{
    private static readonly HashSet<string> FileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile",
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
        "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "TypeScriptCompile",
    };

    public static Dictionary<string, ProjectInfo> Analyze(FullPath inputFilePath, TextWriter? log = null)
    {
        log?.WriteLine($"Building static graph from entry point: {inputFilePath}");

        var entryPoint = new ProjectGraphEntryPoint(inputFilePath);
        using var projectCollection = new ProjectCollection();
        var degreeOfParallelism = Environment.ProcessorCount;

        var graph = new ProjectGraph([entryPoint], projectCollection, projectInstanceFactory: null, degreeOfParallelism, CancellationToken.None);

        log?.WriteLine($"Graph built in {graph.ConstructionMetrics.ConstructionTime.TotalMilliseconds:F0}ms: {graph.ConstructionMetrics.NodeCount} nodes, {graph.ConstructionMetrics.EdgeCount} edges");

        // Deduplicate nodes by project path (multi-targeting produces multiple nodes for the same project)
        // Use topologically sorted order for deterministic processing
        var nodesByPath = new Dictionary<string, List<ProjectGraphNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.ProjectNodesTopologicallySorted)
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
            var isTestProject = false;

            foreach (var node in nodes)
            {
                if (!isTestProject)
                {
                    var propertyValue = node.ProjectInstance.GetPropertyValue("IsTestProject");
                    isTestProject = ProjectInfo.IsTruePropertyValue(propertyValue);
                }

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
                IsTestProject = isTestProject,
            };
        }

        return result;
    }

    private static string NormalizePath(string path)
    {
        return ProjectGraphAnalyzer.NormalizePath(path);
    }
}
