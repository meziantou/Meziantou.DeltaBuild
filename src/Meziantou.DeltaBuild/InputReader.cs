using Meziantou.Framework;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Meziantou.DeltaBuild;

internal static class InputReader
{
    private static readonly HashSet<string> SingleProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".esproj",
        ".wixproj",
        ".sqlproj",
        ".vcxproj",
        ".sfproj",
        ".ccproj",
    };

    private static readonly HashSet<string> AllProjectExtensions = new(SingleProjectExtensions, StringComparer.OrdinalIgnoreCase)
    {
        ".proj",
    };

    public static async Task<InputModel> ReadAsync(string inputPath, CancellationToken cancellationToken)
    {
        var fullPath = FullPath.FromPath(inputPath);
        var extension = fullPath.Extension;

        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
            return await ReadSlnAsync(fullPath, cancellationToken);

        if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            return await ReadSlnxAsync(fullPath, cancellationToken);

        // .proj files could be Traversal or single projects; check content first
        if (string.Equals(extension, ".proj", StringComparison.OrdinalIgnoreCase))
            return ReadTraversalAsync(fullPath);

        if (IsSingleProjectExtension(extension))
            return ReadSingleProject(fullPath);

        return ReadTraversalAsync(fullPath);
    }

    private static bool IsSingleProjectExtension(string extension)
    {
        return SingleProjectExtensions.Contains(extension);
    }

    private static bool IsAllProjectExtension(string extension)
    {
        return AllProjectExtensions.Contains(extension);
    }

    private static async Task<InputModel> ReadSlnAsync(FullPath fullPath, CancellationToken cancellationToken)
    {
        var solution = await SolutionSerializers.SlnFileV12.OpenAsync(fullPath, cancellationToken);
        var projectPaths = ExtractProjectPathsFromSolution(solution, fullPath);

        return new InputModel
        {
            Format = InputFormat.Sln,
            InputFilePath = fullPath,
            ProjectAbsolutePaths = projectPaths,
            SolutionModel = solution,
        };
    }

    private static async Task<InputModel> ReadSlnxAsync(FullPath fullPath, CancellationToken cancellationToken)
    {
        var solution = await SolutionSerializers.SlnXml.OpenAsync(fullPath, cancellationToken);
        var projectPaths = ExtractProjectPathsFromSolution(solution, fullPath);

        return new InputModel
        {
            Format = InputFormat.Slnx,
            InputFilePath = fullPath,
            ProjectAbsolutePaths = projectPaths,
            SolutionModel = solution,
        };
    }

    private static List<FullPath> ExtractProjectPathsFromSolution(SolutionModel solution, FullPath solutionPath)
    {
        var solutionDir = solutionPath.Parent;
        var paths = new List<FullPath>();

        foreach (var project in solution.SolutionProjects)
        {
            var projectPath = project.FilePath;
            if (projectPath is null)
                continue;

            var absolutePath = FullPath.Combine(solutionDir, projectPath);
            if (!IsAllProjectExtension(absolutePath.Extension))
                continue;

            paths.Add(absolutePath);
        }

        return paths;
    }

    private static InputModel ReadTraversalAsync(FullPath fullPath)
    {
        // Use MSBuild evaluation to properly handle globbing and Include/Remove semantics
        var paths = MsBuildTraversalReader.GetProjectReferences(fullPath);

        // If no ProjectReference items found, it might be a single project file
        if (paths.Count == 0 && IsSingleProjectExtension(fullPath.Extension))
        {
            return ReadSingleProject(fullPath);
        }

        return new InputModel
        {
            Format = InputFormat.Traversal,
            InputFilePath = fullPath,
            ProjectAbsolutePaths = paths,
        };
    }

    private static InputModel ReadSingleProject(FullPath fullPath)
    {
        return new InputModel
        {
            Format = InputFormat.SingleProject,
            InputFilePath = fullPath,
            ProjectAbsolutePaths = [fullPath],
        };
    }
}
