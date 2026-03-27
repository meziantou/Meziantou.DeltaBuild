using Meziantou.Framework;
using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace Meziantou.DeltaBuild;

internal sealed class InputModel
{
    public required InputFormat Format { get; init; }
    public required FullPath InputFilePath { get; init; }
    public required IReadOnlyList<FullPath> ProjectAbsolutePaths { get; init; }

    /// <summary>
    /// The original SolutionModel, preserved for round-trip output (SLN/SLNX).
    /// </summary>
    public SolutionModel? SolutionModel { get; init; }
}
