using Microsoft.Build.Execution;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Resolves which MSBuild item types and files are tracked as owned files for a project.
/// Projects can extend the built-in item types with the <c>DeltaBuildItems</c> item, add or remove
/// individual files with the <c>DeltaBuildIncludeFile</c> and <c>DeltaBuildExcludeFile</c> items,
/// and opt out of the built-in item types with the <c>DeltaBuildItemsIncludeDefaults</c> property.
/// Must be used AFTER MSBuildLocator.RegisterDefaults().
/// </summary>
internal static class DeltaBuildItems
{
    public const string ItemTypesItemName = "DeltaBuildItems";
    public const string IncludeFileItemName = "DeltaBuildIncludeFile";
    public const string ExcludeFileItemName = "DeltaBuildExcludeFile";
    public const string IncludeDefaultsPropertyName = "DeltaBuildItemsIncludeDefaults";

    /// <summary>
    /// Item types tracked by default when all files come from MSBuild evaluation.
    /// </summary>
    public static readonly IReadOnlyCollection<string> DefaultItemTypes =
    [
        "Compile",
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
        "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "TypeScriptCompile",
        "Protobuf",
    ];

    /// <summary>
    /// Item types tracked by default when source files already come from another source
    /// (Roslyn's <c>Documents</c> and <c>AdditionalDocuments</c> collections).
    /// </summary>
    public static readonly IReadOnlyCollection<string> DefaultNonSourceItemTypes =
    [
        "Content",
        "None",
        "EmbeddedResource",
        "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "TypeScriptCompile",
        "Protobuf",
    ];

    /// <summary>
    /// Computes the item types to track for a single evaluated project.
    /// </summary>
    public static HashSet<string> GetItemTypes(ProjectInstance projectInstance, IReadOnlyCollection<string> defaultItemTypes)
    {
        var includeDefaults = !ProjectInfo.IsFalsePropertyValue(projectInstance.GetPropertyValue(IncludeDefaultsPropertyName));
        var itemTypes = includeDefaults
            ? new HashSet<string>(defaultItemTypes, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in projectInstance.GetItems(ItemTypesItemName))
        {
            var itemType = item.EvaluatedInclude.Trim();
            if (itemType.Length > 0)
            {
                itemTypes.Add(itemType);
            }
        }

        return itemTypes;
    }

    /// <summary>
    /// Adds the files declared by the <c>DeltaBuildIncludeFile</c> item to the owned files of a project.
    /// </summary>
    public static void AddIncludedFiles(ProjectInstance projectInstance, HashSet<string> ownedFiles)
    {
        AddFiles(projectInstance, IncludeFileItemName, ownedFiles);
    }

    /// <summary>
    /// Adds the files declared by the <c>DeltaBuildExcludeFile</c> item to the files to remove
    /// from the owned files of a project. Exclusions are applied after all files are collected.
    /// </summary>
    public static void AddExcludedFiles(ProjectInstance projectInstance, HashSet<string> excludedFiles)
    {
        AddFiles(projectInstance, ExcludeFileItemName, excludedFiles);
    }

    private static void AddFiles(ProjectInstance projectInstance, string itemName, HashSet<string> files)
    {
        foreach (var item in projectInstance.GetItems(itemName))
        {
            var fullPath = item.GetMetadataValue("FullPath");
            if (!string.IsNullOrEmpty(fullPath))
            {
                files.Add(ProjectGraphAnalyzer.NormalizePath(fullPath));
            }
        }
    }
}
