using System.Text.Json;
using System.Xml.Linq;
using Meziantou.Framework;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Meziantou.DeltaBuild;

internal static class OutputWriter
{
    public static async Task WriteAsync(
        DeltaBuildOptions options,
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var outputPath = FullPath.FromPath(options.OutputPath);

        if (options.Shard is { } shard && options.TotalShards is { } totalShards)
        {
            await WriteShardAsync(options, input, affectedProjectPaths, outputPath, shard, totalShards, log, cancellationToken);
            return;
        }

        if (options.NoOutputIfEmpty && affectedProjectPaths.Count is 0)
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
                log.WriteLine($"No projects were affected. Deleted existing output file at {outputPath}.");
            }
            else
            {
                log.WriteLine($"No projects were affected. Skipped generating output file at {outputPath}.");
            }

            return;
        }

        await WriteSingleOutputAsync(options, input, affectedProjectPaths, outputPath, log, cancellationToken);
    }

    private static async Task WriteSingleOutputAsync(
        DeltaBuildOptions options,
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var extension = outputPath.Extension;

        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSlnAsync(input, affectedProjectPaths, outputPath, log, cancellationToken);
        }
        else if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSlnxAsync(input, affectedProjectPaths, outputPath, log, cancellationToken);
        }
        else if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(affectedProjectPaths, outputPath, log, cancellationToken);
        }
        else if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTxtAsync(affectedProjectPaths, outputPath, log, cancellationToken);
        }
        else
        {
            WriteTraversal(options, affectedProjectPaths, outputPath, log);
        }

        log.WriteLine($"Output written to {outputPath}");
    }

    private static async Task WriteShardAsync(
        DeltaBuildOptions options,
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        int shard,
        int totalShards,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var orderedProjects = affectedProjectPaths
            .OrderBy(projectPath => projectPath, FullPathComparer.Default)
            .ToList();
        var repositoryPath = FullPath.FromPath(options.RepositoryPath);
        var shardProjects = GetShardProjects(
            orderedProjects,
            shard,
            totalShards,
            options.ShardSeparateProjects,
            repositoryPath);

        if (options.NoOutputIfEmpty && shardProjects.Count is 0)
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
                log.WriteLine($"Shard {shard}/{totalShards} contains no projects. Deleted existing output file at {outputPath}.");
            }
            else
            {
                log.WriteLine($"Shard {shard}/{totalShards} contains no projects. Skipped generating output file at {outputPath}.");
            }

            return;
        }

        log.WriteLine($"Selected shard {shard}/{totalShards}: {shardProjects.Count} project(s) out of {orderedProjects.Count} affected project(s).");
        await WriteSingleOutputAsync(options, input, shardProjects, outputPath, log, cancellationToken);
    }

    private static List<List<FullPath>> SplitIntoShards(List<FullPath> projects, int shardCount)
    {
        var result = new List<List<FullPath>>(shardCount);
        var minimumShardSize = projects.Count / shardCount;
        var shardCountWithExtraProject = projects.Count % shardCount;
        var currentIndex = 0;

        for (var i = 0; i < shardCount; i++)
        {
            var shardSize = minimumShardSize + (i < shardCountWithExtraProject ? 1 : 0);
            var shardProjects = new List<FullPath>(shardSize);
            for (var j = 0; j < shardSize; j++)
            {
                shardProjects.Add(projects[currentIndex]);
                currentIndex++;
            }

            result.Add(shardProjects);
        }

        return result;
    }

    private static List<FullPath> GetShardProjects(
        List<FullPath> projects,
        int shard,
        int totalShards,
        string[] shardSeparateProjects,
        FullPath repositoryPath)
    {
        if (shardSeparateProjects.Length == 0)
        {
            var shardedProjects = SplitIntoShards(projects, totalShards);
            return shardedProjects[shard - 1];
        }

        var separatedProjectsByPath = ParseShardSeparateProjectPaths(shardSeparateProjects, repositoryPath);
        var projectsByPath = projects.ToDictionary(
            path => ProjectGraphAnalyzer.NormalizePath(path),
            path => path,
            StringComparer.OrdinalIgnoreCase);
        var selectedProjectsByPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shardAssignments = new List<List<FullPath>>(totalShards);

        for (var i = 0; i < totalShards; i++)
        {
            shardAssignments.Add([]);
        }

        var selectedProjectIndex = 0;
        foreach (var separatedProjectPath in separatedProjectsByPath)
        {
            if (!projectsByPath.TryGetValue(separatedProjectPath, out var projectPath))
            {
                continue;
            }

            if (!selectedProjectsByPath.Add(separatedProjectPath))
            {
                continue;
            }

            shardAssignments[selectedProjectIndex % totalShards].Add(projectPath);
            selectedProjectIndex++;
        }

        var remainingProjects = new List<FullPath>(projects.Count - selectedProjectsByPath.Count);
        foreach (var projectPath in projects)
        {
            var normalizedProjectPath = ProjectGraphAnalyzer.NormalizePath(projectPath);
            if (!selectedProjectsByPath.Contains(normalizedProjectPath))
            {
                remainingProjects.Add(projectPath);
            }
        }

        var remainingAssignments = SplitIntoShards(remainingProjects, totalShards);
        for (var i = 0; i < totalShards; i++)
        {
            shardAssignments[i].AddRange(remainingAssignments[i]);
        }

        return shardAssignments[shard - 1];
    }

    private static string[] ParseShardSeparateProjectPaths(string[] projectPaths, FullPath repositoryPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(projectPaths.Length);
        foreach (var projectPath in projectPaths)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new InvalidOperationException("The --shard-separate option cannot contain an empty project path.");
            }

            var trimmedProjectPath = projectPath.Trim();
            FullPath absolutePath;
            if (Path.IsPathRooted(trimmedProjectPath))
            {
                absolutePath = FullPath.FromPath(trimmedProjectPath);
            }
            else
            {
                var normalizedPath = trimmedProjectPath.Replace('\\', '/');
                absolutePath = FullPath.Combine(repositoryPath, normalizedPath);
            }

            var normalizedAbsolutePath = ProjectGraphAnalyzer.NormalizePath(absolutePath);
            if (seen.Add(normalizedAbsolutePath))
            {
                result.Add(normalizedAbsolutePath);
            }
        }

        return [.. result];
    }

    private static async Task WriteSlnAsync(
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var solution = BuildFilteredSolution(input, affectedProjectPaths, outputPath, log);
        outputPath.CreateParentDirectory();
        await SolutionSerializers.SlnFileV12.SaveAsync(outputPath, solution, cancellationToken);
    }

    private static async Task WriteSlnxAsync(
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var solution = BuildFilteredSolution(input, affectedProjectPaths, outputPath, log);
        outputPath.CreateParentDirectory();
        await SolutionSerializers.SlnXml.SaveAsync(outputPath, solution, cancellationToken);
    }

    private static SolutionModel BuildFilteredSolution(
        InputModel input,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log)
    {
        var outputDir = outputPath.Parent;

        var affectedSet = new HashSet<string>(
            affectedProjectPaths.Select(p => ProjectGraphAnalyzer.NormalizePath(p)),
            StringComparer.OrdinalIgnoreCase);

        var solution = new SolutionModel();

        // Copy build types and platforms from original if available
        if (input.SolutionModel is not null)
        {
            foreach (var buildType in input.SolutionModel.BuildTypes)
            {
                solution.AddBuildType(buildType);
            }

            foreach (var platform in input.SolutionModel.Platforms)
            {
                solution.AddPlatform(platform);
            }
        }

        if (input.SolutionModel is not null)
        {
            // Build a map of folder paths for projects that should be included
            var foldersToCreate = new Dictionary<string, SolutionFolderModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var srcProject in input.SolutionModel.SolutionProjects)
            {
                if (srcProject.FilePath is null)
                    continue;

                var absolutePath = FullPath.Combine(input.InputFilePath.Parent, srcProject.FilePath);
                var normalizedPath = ProjectGraphAnalyzer.NormalizePath(absolutePath);

                if (!affectedSet.Contains(normalizedPath))
                    continue;

                // Recreate folder hierarchy
                SolutionFolderModel? targetFolder = null;
                if (srcProject.Parent is SolutionFolderModel srcFolder)
                {
                    targetFolder = EnsureFolder(solution, srcFolder, foldersToCreate);
                }

                // Compute relative path from output directory
                var relativePath = ToRelativePath(absolutePath, outputDir);

                var tgtProject = solution.AddProject(relativePath, projectTypeName: null, folder: targetFolder);

                // Copy project configuration rules
                if (srcProject.ProjectConfigurationRules is { } rules)
                {
                    foreach (var rule in rules)
                    {
                        tgtProject.AddProjectConfigurationRule(rule);
                    }
                }
            }
        }
        else
        {
            // No original solution model — just add projects without folder structure
            foreach (var projectPath in affectedProjectPaths)
            {
                var relativePath = ToRelativePath(projectPath, outputDir);
                solution.AddProject(relativePath);
            }
        }

        log.WriteLine($"Solution contains {solution.SolutionProjects.Count} project(s)");
        return solution;
    }

    private static SolutionFolderModel EnsureFolder(
        SolutionModel solution,
        SolutionFolderModel srcFolder,
        Dictionary<string, SolutionFolderModel> foldersToCreate)
    {
        var folderPath = GetFolderPath(srcFolder);

        if (foldersToCreate.TryGetValue(folderPath, out var existing))
            return existing;

        if (srcFolder.Parent is SolutionFolderModel parentSrcFolder &&
            parentSrcFolder.Parent is not null) // Parent is not the root SolutionModel
        {
            _ = EnsureFolder(solution, parentSrcFolder, foldersToCreate);
        }

        var newFolder = solution.AddFolder(folderPath);
        foldersToCreate[folderPath] = newFolder;
        return newFolder;
    }

    private static string GetFolderPath(SolutionFolderModel folder)
    {
        return folder.Path ?? ("/" + folder.Name + "/");
    }

    private static string ToRelativePath(FullPath path, FullPath basePath)
    {
        return path.MakePathRelativeTo(basePath).Replace('\\', '/');
    }

    private static string ToTraversalPath(FullPath path, FullPath basePath)
    {
        return ToTraversalPath(ToRelativePath(path, basePath));
    }

    private static string ToTraversalPath(string path)
    {
        var normalizedPath = path.Replace('\\', '/');

        if (Path.IsPathRooted(normalizedPath) ||
            normalizedPath.StartsWith("$(", StringComparison.Ordinal))
        {
            return normalizedPath;
        }

        if (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        return "$(MSBuildThisFileDirectory)" + normalizedPath;
    }

    private static async Task WriteJsonAsync(
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var outputDir = outputPath.Parent;
        outputPath.CreateParentDirectory();

        var relativePaths = affectedProjectPaths
            .OrderBy(p => p, FullPathComparer.Default)
            .Select(p => ToRelativePath(p, outputDir))
            .ToArray();

        using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, relativePaths, JsonSerializerOptions.Default, cancellationToken);

        log.WriteLine($"JSON file contains {affectedProjectPaths.Count} project(s)");
    }

    private static async Task WriteTxtAsync(
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var outputDir = outputPath.Parent;
        outputPath.CreateParentDirectory();

        var lines = affectedProjectPaths
            .OrderBy(p => p, FullPathComparer.Default)
            .Select(p => ToRelativePath(p, outputDir))
            .ToArray();

        await File.WriteAllLinesAsync(outputPath, lines, cancellationToken);

        log.WriteLine($"Text file contains {affectedProjectPaths.Count} project(s)");
    }

    private static void WriteTraversal(
        DeltaBuildOptions options,
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log)
    {
        var outputDir = outputPath.Parent;
        outputPath.CreateParentDirectory();

        var outputFileName = Path.GetFileName(outputPath.Value);
        var beforeImportPath = options.TraversalBeforeImport ?? (Path.GetFileNameWithoutExtension(outputFileName) + ".before.proj");
        var afterImportPath = options.TraversalAfterImport ?? (Path.GetFileNameWithoutExtension(outputFileName) + ".after.proj");
        var traversalBeforeImportPath = ToTraversalPath(beforeImportPath);
        var traversalAfterImportPath = ToTraversalPath(afterImportPath);
        var traversalSdk = "Microsoft.Build.Traversal";
        if (!string.IsNullOrWhiteSpace(options.TraversalSdkVersion))
        {
            var version = options.TraversalSdkVersion.Trim().TrimStart('/');
            traversalSdk += "/" + version;
        }

        var doc = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", traversalSdk)));

        var root = doc.Root!;

        root.Add(new XElement("PropertyGroup",
            new XElement("IsTraversal", "true")));

        // Add conditional before-import
        root.Add(new XElement("Import",
            new XAttribute("Project", traversalBeforeImportPath),
            new XAttribute("Condition", $"Exists('{traversalBeforeImportPath}')")));

        // Add project references
        var itemGroup = new XElement("ItemGroup");

        foreach (var projectPath in affectedProjectPaths.OrderBy(p => p, FullPathComparer.Default))
        {
            var traversalPath = ToTraversalPath(projectPath, outputDir);
            itemGroup.Add(new XElement("ProjectReference", new XAttribute("Include", traversalPath)));
        }

        root.Add(itemGroup);

        // Add conditional after-import
        root.Add(new XElement("Import",
            new XAttribute("Project", traversalAfterImportPath),
            new XAttribute("Condition", $"Exists('{traversalAfterImportPath}')")));

        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            NewLineChars = "\n",
        };

        using (var writer = System.Xml.XmlWriter.Create(outputPath, settings))
        {
            doc.Save(writer);
        }

        log.WriteLine($"Traversal file contains {affectedProjectPaths.Count} project(s)");
    }
}
