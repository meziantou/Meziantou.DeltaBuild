using Microsoft.Build.Execution;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Collects the NuGet package information of a project from its evaluations (one per target framework).
/// This is used to find the projects affected by a change of a <c>PackageVersion</c> item in <c>Directory.Packages.props</c>.
/// Must be used AFTER MSBuildLocator.RegisterDefaults().
/// </summary>
internal sealed class PackageReferenceCollector
{
    private static readonly string[] PackageItemTypes = ["PackageReference", "PackageDownload"];

    private readonly HashSet<string> _packageReferences = new(StringComparer.OrdinalIgnoreCase);
    private bool _isIncomplete;

    public bool UsesTransitivePinning { get; private set; }

    /// <summary>
    /// Gets the IDs of the referenced packages, or <see langword="null"/> when they are unknown because an evaluation failed.
    /// </summary>
    public HashSet<string>? GetPackageReferences()
    {
        return _isIncomplete ? null : _packageReferences;
    }

    public void MarkIncomplete()
    {
        _isIncomplete = true;
    }

    public string? ProjectAssetsFile { get; private set; }

    public void Add(ProjectInstance projectInstance)
    {
        foreach (var itemType in PackageItemTypes)
        {
            foreach (var item in projectInstance.GetItems(itemType))
            {
                var packageId = item.EvaluatedInclude.Trim();
                if (packageId.Length > 0)
                {
                    _packageReferences.Add(packageId);
                }
            }
        }

        if (!UsesTransitivePinning)
        {
            UsesTransitivePinning = ProjectInfo.IsTruePropertyValue(projectInstance.GetPropertyValue("CentralPackageTransitivePinningEnabled"));
        }

        if (ProjectAssetsFile is null)
        {
            var projectAssetsFile = projectInstance.GetPropertyValue("ProjectAssetsFile");
            if (string.IsNullOrEmpty(projectAssetsFile))
            {
                var projectExtensionsPath = projectInstance.GetPropertyValue("MSBuildProjectExtensionsPath");
                if (!string.IsNullOrEmpty(projectExtensionsPath))
                {
                    projectAssetsFile = Path.Combine(projectExtensionsPath, "project.assets.json");
                }
            }

            if (!string.IsNullOrEmpty(projectAssetsFile))
            {
                ProjectAssetsFile = Path.GetFullPath(projectAssetsFile, projectInstance.Directory);
            }
        }
    }
}
