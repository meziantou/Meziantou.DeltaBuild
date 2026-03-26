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
            WriteTraversal(affectedProjectPaths, outputPath, log);
        }

        log.WriteLine($"Output written to {outputPath}");
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
        return folder.Name ?? "";
    }

    private static string ToRelativePath(FullPath path, FullPath basePath)
    {
        return path.MakePathRelativeTo(basePath).Replace('\\', '/');
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
        IReadOnlyList<FullPath> affectedProjectPaths,
        FullPath outputPath,
        TextWriter log)
    {
        var outputDir = outputPath.Parent;
        outputPath.CreateParentDirectory();

        var outputFileName = Path.GetFileName(outputPath.Value);
        var beforeImportPath = Path.GetFileNameWithoutExtension(outputFileName) + ".before.proj";
        var afterImportPath = Path.GetFileNameWithoutExtension(outputFileName) + ".after.proj";

        var doc = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.Build.Traversal")));

        var root = doc.Root!;

        // Add conditional before-import
        root.Add(new XElement("Import",
            new XAttribute("Project", beforeImportPath),
            new XAttribute("Condition", $"Exists('{beforeImportPath}')")));

        // Add project references
        var itemGroup = new XElement("ItemGroup");

        foreach (var projectPath in affectedProjectPaths.OrderBy(p => p, FullPathComparer.Default))
        {
            var relativePath = ToRelativePath(projectPath, outputDir);
            itemGroup.Add(new XElement("ProjectReference", new XAttribute("Include", relativePath)));
        }

        root.Add(itemGroup);

        // Add conditional after-import
        root.Add(new XElement("Import",
            new XAttribute("Project", afterImportPath),
            new XAttribute("Condition", $"Exists('{afterImportPath}')")));

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
