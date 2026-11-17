using Meziantou.Framework;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
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
        IReadOnlySet<string> changedFiles,
        string? whereCondition,
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

        // Share the evaluation caches (file system, imports, globs) between the evaluations of the projects
        using var projectCollection = new ProjectCollection();
        var projectOptions = new ProjectOptions
        {
            ProjectCollection = projectCollection,
            EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared),
        };

        // Build the result dictionary
        var result = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (projectPath, roslynProjects) in projectsByPath)
        {
            var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isTestProject = false;
            var packages = new PackageReferenceCollector();

            // Include the project file itself
            if (changedFiles.Contains(projectPath))
            {
                ownedFiles.Add(projectPath);
            }

            foreach (var roslynProject in roslynProjects)
            {
                // Source documents (Compile items as seen by Roslyn — the key safety improvement)
                foreach (var doc in roslynProject.Documents)
                {
                    if (doc.FilePath is not null)
                    {
                        ProjectGraphAnalyzer.AddIfChanged(doc.FilePath, changedFiles, ownedFiles);
                    }
                }

                // Additional documents (AdditionalFiles items)
                foreach (var doc in roslynProject.AdditionalDocuments)
                {
                    if (doc.FilePath is not null)
                    {
                        ProjectGraphAnalyzer.AddIfChanged(doc.FilePath, changedFiles, ownedFiles);
                    }
                }

                // Analyzer config documents (.editorconfig files, .globalconfig)
                foreach (var doc in roslynProject.AnalyzerConfigDocuments)
                {
                    if (doc.FilePath is not null)
                    {
                        ProjectGraphAnalyzer.AddIfChanged(doc.FilePath, changedFiles, ownedFiles);
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
            ProjectInstance? projectInstance = null;
            try
            {
                projectInstance = ProjectInstance.FromFile(projectPath, projectOptions);
                isTestProject = ProjectInfo.IsTruePropertyValue(projectInstance.GetPropertyValue("IsTestProject"));
                foreach (var importPath in projectInstance.ImportPaths)
                {
                    ProjectGraphAnalyzer.AddIfChanged(importPath, changedFiles, ownedFiles);
                }

                // Also include non-source items that Roslyn doesn't track (Content, None, EmbeddedResource, etc.)
                var fileItemTypes = DeltaBuildItems.GetItemTypes(projectInstance, DeltaBuildItems.DefaultNonSourceItemTypes);
                DeltaBuildItems.AddItemFiles(projectInstance, fileItemTypes, changedFiles, ownedFiles);
                DeltaBuildItems.AddIncludedFiles(projectInstance, changedFiles, ownedFiles);

                // Remove the files excluded by DeltaBuildExcludeFile
                var excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DeltaBuildItems.AddExcludedFiles(projectInstance, changedFiles, excludedFiles);
                ownedFiles.ExceptWith(excludedFiles);

                // The outer build of a multi-targeted project doesn't see the packages referenced per target framework.
                // Evaluating each target framework is only needed when a changed Directory.Packages.props is imported.
                packages.Add(projectInstance);
                if (ownedFiles.Any(CentralPackageVersionsDiff.IsCentralPackageVersionsFile))
                {
                    foreach (var targetFrameworkInstance in GetTargetFrameworkInstances(projectInstance, projectOptions))
                    {
                        packages.Add(targetFrameworkInstance);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.WriteLine($"  Warning: Could not evaluate imports for {projectPath}: {ex.Message}");
                packages.MarkIncomplete();
            }

            // Evaluated outside the try/catch so an invalid condition is reported instead of being ignored.
            // A project that could not be evaluated never matches the condition.
            var matchesWhereCondition = whereCondition is null ||
                (projectInstance is not null && MatchesWhereCondition(projectInstance, whereCondition, projectOptions));

            result[projectPath] = new ProjectInfo
            {
                ProjectPath = projectPath,
                OwnedFiles = ownedFiles,
                ReferencedProjectPaths = referencedProjectPaths,
                ReferencingProjectPaths = referencingProjectPaths,
                IsTestProject = isTestProject,
                MatchesWhereCondition = matchesWhereCondition,
                PackageReferences = packages.GetPackageReferences(),
                UsesTransitivePinning = packages.UsesTransitivePinning,
                ProjectAssetsFile = packages.ProjectAssetsFile,
            };
        }

        return result;
    }

    private static bool MatchesWhereCondition(ProjectInstance projectInstance, string whereCondition, ProjectOptions projectOptions)
    {
        if (ProjectCondition.Evaluate(projectInstance, whereCondition))
            return true;

        // A multi-targeted project is evaluated as its outer build, which doesn't see the properties
        // set per target framework. Evaluate each target framework like the graph-based engines do.
        foreach (var targetFrameworkInstance in GetTargetFrameworkInstances(projectInstance, projectOptions))
        {
            if (ProjectCondition.Evaluate(targetFrameworkInstance, whereCondition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates each target framework of a multi-targeted project. Returns nothing for a project that is not multi-targeted.
    /// </summary>
    private static IEnumerable<ProjectInstance> GetTargetFrameworkInstances(ProjectInstance projectInstance, ProjectOptions projectOptions)
    {
        if (!string.IsNullOrEmpty(projectInstance.GetPropertyValue("TargetFramework")))
            yield break;

        var targetFrameworks = projectInstance.GetPropertyValue("TargetFrameworks")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var targetFramework in targetFrameworks)
        {
            var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TargetFramework"] = targetFramework,
            };

            yield return ProjectInstance.FromFile(projectInstance.FullPath, new ProjectOptions
            {
                GlobalProperties = globalProperties,
                ProjectCollection = projectOptions.ProjectCollection,
                EvaluationContext = projectOptions.EvaluationContext,
            });
        }
    }

    private static string NormalizePath(string path)
    {
        return ProjectGraphAnalyzer.NormalizePath(path);
    }
}
