namespace Meziantou.DeltaBuild;

internal sealed class ProjectInfo
{
    public required string ProjectPath { get; init; }

    /// <summary>
    /// The normalized paths of the files owned by the project. Only the changed files are tracked.
    /// </summary>
    public required HashSet<string> OwnedFiles { get; init; }
    public required HashSet<string> ReferencedProjectPaths { get; init; }
    public required HashSet<string> ReferencingProjectPaths { get; init; }
    public required bool IsTestProject { get; init; }
    public required bool MatchesWhereCondition { get; init; }

    /// <summary>
    /// The IDs of the packages referenced by the project (<c>PackageReference</c> and <c>PackageDownload</c> items),
    /// or <see langword="null"/> when they are unknown.
    /// </summary>
    public required HashSet<string>? PackageReferences { get; init; }

    /// <summary>
    /// Whether <c>CentralPackageTransitivePinningEnabled</c> is enabled for the project.
    /// </summary>
    public required bool UsesTransitivePinning { get; init; }

    /// <summary>
    /// The path of the NuGet assets file (<c>project.assets.json</c>) of the project, if known.
    /// </summary>
    public required string? ProjectAssetsFile { get; init; }

    public static bool IsTruePropertyValue(string? value)
    {
        return bool.TryParse(value?.Trim(), out var result) && result;
    }

    public static bool IsFalsePropertyValue(string? value)
    {
        return bool.TryParse(value?.Trim(), out var result) && !result;
    }
}
