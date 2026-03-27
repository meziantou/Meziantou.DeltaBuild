using Meziantou.Framework;
using Microsoft.Build.Evaluation;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Uses MSBuild evaluation to read ProjectReference items from a traversal project file.
/// This properly handles globbing patterns (e.g., src/**/*.*proj) and Include/Remove semantics.
/// Must be called AFTER MSBuildLocator.RegisterDefaults() and kept in a separate class
/// to avoid loading Microsoft.Build assemblies too early.
/// </summary>
internal static class MsBuildTraversalReader
{
    public static List<FullPath> GetProjectReferences(FullPath projectFilePath)
    {
        using var projectCollection = new ProjectCollection();
        var project = new Project(projectFilePath.Value, globalProperties: null, toolsVersion: null, projectCollection);

        var projectDir = projectFilePath.Parent;
        var paths = new List<FullPath>();

        foreach (var item in project.GetItems("ProjectReference"))
        {
            var evaluatedInclude = item.EvaluatedInclude;
            if (string.IsNullOrEmpty(evaluatedInclude))
                continue;

            var absolutePath = FullPath.Combine(projectDir, evaluatedInclude);
            paths.Add(absolutePath);
        }

        return paths;
    }
}
