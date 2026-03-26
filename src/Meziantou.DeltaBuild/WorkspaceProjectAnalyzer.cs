using Meziantou.Framework;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis.MSBuild;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Uses Roslyn's MSBuildWorkspace to load projects and discover files.
/// This is more compatible than the StaticGraph approach because Roslyn sees
/// files added dynamically by MSBuild targets (e.g., source generators, custom targets).
/// Import tracking still uses MSBuild's ProjectInstance for completeness.
/// Must be called AFTER MSBuildLocator.RegisterDefaults() and in a separate class
/// to avoid loading Microsoft.Build/Roslyn assemblies too early.
/// </summary>
internal static class WorkspaceProjectAnalyzer
{
    public static async Task<Dictionary<string, ProjectInfo>> AnalyzeAsync(
        IReadOnlyList<FullPath> projectPaths,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
        log?.WriteLine($"Opening {projectPaths.Count} project(s) in Roslyn workspace...");

        using var workspace = MSBuildWorkspace.Create();

        // Allow loading projects that Roslyn doesn't fully recognize (e.g., F#)
        workspace.SkipUnrecognizedProjects = false;

        // Roslyn doesn't natively support F# but we can load .fsproj for file tracking
        workspace.AssociateFileExtensionWithLanguage("fsproj", Microsoft.CodeAnalysis.LanguageNames.CSharp);

        // Log workspace issues
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            if (args.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
            {
                log?.WriteLine($"  Workspace error: {args.Diagnostic.Message}");
            }
            else
            {
                log?.WriteLine($"  Workspace warning: {args.Diagnostic.Message}");
            }
        });

        // Open all projects into the workspace
        // Note: OpenProjectAsync also loads transitive dependencies, so we skip projects already loaded
        var progress = new Progress<ProjectLoadProgress>(p =>
        {
            log?.WriteLine($"  [{p.ElapsedTime:g}] {p.Operation} {p.FilePath}");
        });

        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if this project was already loaded as a transitive dependency of a previous project
            var alreadyLoaded = workspace.CurrentSolution.Projects
                .Any(p => string.Equals(p.FilePath, projectPath.Value, StringComparison.OrdinalIgnoreCase));

            if (alreadyLoaded)
            {
                log?.WriteLine($"  Already loaded: {projectPath}");
                continue;
            }

            log?.WriteLine($"  Loading project: {projectPath}");
            await workspace.OpenProjectAsync(projectPath.Value, progress, cancellationToken);
        }

        var solution = workspace.CurrentSolution;
        var depGraph = solution.GetProjectDependencyGraph();

        log?.WriteLine($"Workspace loaded: {solution.Projects.Count()} project(s)");

        // Deduplicate projects by path (multi-targeting may produce multiple Roslyn Project objects per file)
        var projectsByPath = new Dictionary<string, List<Microsoft.CodeAnalysis.Project>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null)
                continue;

            var normalizedPath = NormalizePath(project.FilePath);
            if (!projectsByPath.TryGetValue(normalizedPath, out var list))
            {
                list = [];
                projectsByPath[normalizedPath] = list;
            }

            list.Add(project);
        }

        // Build the result dictionary
        var result = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (projectPath, roslynProjects) in projectsByPath)
        {
            var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Include the project file itself
            ownedFiles.Add(projectPath);

            foreach (var roslynProject in roslynProjects)
            {
                // Source documents (Compile items as seen by Roslyn — the key safety improvement)
                foreach (var doc in roslynProject.Documents)
                {
                    if (doc.FilePath is not null)
                    {
                        ownedFiles.Add(NormalizePath(doc.FilePath));
                    }
                }

                // Additional documents (AdditionalFiles items)
                foreach (var doc in roslynProject.AdditionalDocuments)
                {
                    if (doc.FilePath is not null)
                    {
                        ownedFiles.Add(NormalizePath(doc.FilePath));
                    }
                }

                // Analyzer config documents (.editorconfig files, .globalconfig)
                foreach (var doc in roslynProject.AnalyzerConfigDocuments)
                {
                    if (doc.FilePath is not null)
                    {
                        ownedFiles.Add(NormalizePath(doc.FilePath));
                    }
                }

                // Forward dependencies (projects this project references)
                foreach (var projRef in roslynProject.ProjectReferences)
                {
                    var refProject = solution.GetProject(projRef.ProjectId);
                    if (refProject?.FilePath is not null)
                    {
                        referencedProjectPaths.Add(NormalizePath(refProject.FilePath));
                    }
                }

                // Reverse dependencies (projects that depend on this project)
                foreach (var depId in depGraph.GetProjectsThatDirectlyDependOnThisProject(roslynProject.Id))
                {
                    var depProject = solution.GetProject(depId);
                    if (depProject?.FilePath is not null)
                    {
                        referencingProjectPaths.Add(NormalizePath(depProject.FilePath));
                    }
                }
            }

            // Also track imported files (.props, .targets) via MSBuild evaluation.
            // Roslyn doesn't expose import paths, so we use ProjectInstance for this.
            try
            {
                var projectInstance = new ProjectInstance(projectPath);
                foreach (var importPath in projectInstance.ImportPaths)
                {
                    ownedFiles.Add(NormalizePath(importPath));
                }

                // Also include non-source items that Roslyn doesn't track (Content, None, EmbeddedResource, etc.)
                foreach (var item in projectInstance.Items)
                {
                    if (NonSourceFileItemTypes.Contains(item.ItemType))
                    {
                        var fullPath = item.GetMetadataValue("FullPath");
                        if (!string.IsNullOrEmpty(fullPath))
                        {
                            ownedFiles.Add(NormalizePath(fullPath));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.WriteLine($"  Warning: Could not evaluate imports for {projectPath}: {ex.Message}");
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
    /// Non-source item types that Roslyn doesn't track via project.Documents.
    /// These are tracked via MSBuild's ProjectInstance to ensure complete coverage.
    /// </summary>
    private static readonly HashSet<string> NonSourceFileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content",
        "None",
        "EmbeddedResource",
        "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "TypeScriptCompile",
    };

    private static string NormalizePath(string path)
    {
        return ProjectGraphAnalyzer.NormalizePath(path);
    }
}
