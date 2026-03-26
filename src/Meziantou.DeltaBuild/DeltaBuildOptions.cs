namespace Meziantou.DeltaBuild;

internal sealed class DeltaBuildOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required string RepositoryPath { get; init; }
    public string? HeadCommit { get; init; }
    public string? BaseCommit { get; init; }
    public string? BaseBranch { get; init; }
    public string[] IncludePatterns { get; init; } = [];
    public string[] FullRebuildTriggerPatterns { get; init; } = [];
}
