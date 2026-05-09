namespace Meziantou.DeltaBuild;

internal sealed class ProjectInfo
{
    public required string ProjectPath { get; init; }
    public required HashSet<string> OwnedFiles { get; init; }
    public required HashSet<string> ReferencedProjectPaths { get; init; }
    public required HashSet<string> ReferencingProjectPaths { get; init; }
    public required bool IsTestProject { get; init; }

    public static bool IsTruePropertyValue(string? value)
    {
        return bool.TryParse(value?.Trim(), out var result) && result;
    }
}
