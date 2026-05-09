namespace Meziantou.DeltaBuild;

internal sealed class DeltaBuildOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public bool NoOutputIfEmpty { get; init; }
    public required string RepositoryPath { get; init; }
    public string? HeadCommit { get; init; }
    public string? BaseCommit { get; init; }
    public string? BaseBranch { get; init; }
    public bool CompareWorkingTree { get; init; }
    public string[] IncludePatterns { get; init; } = [];
    public bool TestProjectsOnly { get; init; }
    public int? Shard { get; init; }
    public int? TotalShards { get; init; }
    public string[] FullRebuildTriggerPatterns { get; init; } = [];
    public string[] HierarchicalRebuildTriggerPatterns { get; init; } = [];
    public string[] ProjectBundles { get; init; } = [];
    public AnalysisEngine Engine { get; init; } = AnalysisEngine.MSBuild;
    public string? TraversalSdkVersion { get; init; }
    public string? TraversalBeforeImport { get; init; }
    public string? TraversalAfterImport { get; init; }
}
