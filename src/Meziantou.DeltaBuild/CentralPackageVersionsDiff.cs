using System.Xml;
using System.Xml.Linq;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Computes the packages whose <c>PackageVersion</c> items changed between two versions of a
/// Central Package Management file (<c>Directory.Packages.props</c>).
/// </summary>
internal static class CentralPackageVersionsDiff
{
    public const string FileName = "Directory.Packages.props";

    private const string PackageVersionItemName = "PackageVersion";

    public static bool IsCentralPackageVersionsFile(string path)
    {
        return string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the IDs of the packages whose <c>PackageVersion</c> items were added, removed, or modified.
    /// Returns <see langword="null"/> when the change cannot be limited to a set of packages, for instance when
    /// the file was added or deleted, cannot be parsed, or when anything other than <c>PackageVersion</c> items changed.
    /// </summary>
    public static HashSet<string>? GetChangedPackages(string? baseContent, string? headContent)
    {
        if (baseContent is null || headContent is null)
            return null;

        var baseDocument = TryParse(baseContent);
        var headDocument = TryParse(headContent);
        if (baseDocument is null || headDocument is null)
            return null;

        var basePackages = ExtractPackageVersions(baseDocument);
        var headPackages = ExtractPackageVersions(headDocument);
        if (basePackages is null || headPackages is null)
            return null;

        // Any other change (properties, GlobalPackageReference, imports, conditions, ...) may affect all the projects
        if (!XNode.DeepEquals(baseDocument, headDocument))
            return null;

        var changedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, baseDefinitions) in basePackages)
        {
            if (!headPackages.TryGetValue(packageId, out var headDefinitions) || !baseDefinitions.SequenceEqual(headDefinitions, StringComparer.Ordinal))
            {
                changedPackages.Add(packageId);
            }
        }

        foreach (var packageId in headPackages.Keys)
        {
            if (!basePackages.ContainsKey(packageId))
            {
                changedPackages.Add(packageId);
            }
        }

        return changedPackages;
    }

    private static XDocument? TryParse(string content)
    {
        try
        {
            var document = XDocument.Parse(content.TrimStart('\uFEFF'), LoadOptions.None);

            // Comments and processing instructions don't change the evaluation
            document.DescendantNodes().Where(node => node is XComment or XProcessingInstruction).Remove();
            return document;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the <c>PackageVersion</c> items from the document and returns their normalized definitions by package ID.
    /// Returns <see langword="null"/> when a package ID cannot be determined statically.
    /// </summary>
    private static Dictionary<string, List<string>>? ExtractPackageVersions(XDocument document)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var elements = document.Descendants().Where(element => element.Name.LocalName == PackageVersionItemName).ToList();
        foreach (var element in elements)
        {
            var packageId = ((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))?.Trim();
            if (string.IsNullOrEmpty(packageId) || packageId.IndexOfAny(['$', '@', '%', '*', '?', ';']) >= 0)
                return null;

            if (!result.TryGetValue(packageId, out var definitions))
            {
                definitions = [];
                result[packageId] = definitions;
            }

            definitions.Add(GetNormalizedDefinition(element));
        }

        foreach (var element in elements)
        {
            var parent = element.Parent;
            element.Remove();

            // The conditions of the item groups are part of the item definitions, so an item group only containing
            // PackageVersion items can be added or removed without affecting anything else
            if (parent is not null && !parent.Nodes().Any())
            {
                parent.Remove();
            }
        }

        foreach (var definitions in result.Values)
        {
            definitions.Sort(StringComparer.Ordinal);
        }

        return result;
    }

    private static string GetNormalizedDefinition(XElement element)
    {
        var parts = new List<string>();
        foreach (var ancestor in element.Ancestors())
        {
            var condition = (string?)ancestor.Attribute("Condition");
            if (condition is not null)
            {
                parts.Add($"{ancestor.Name.LocalName}.Condition={condition.Trim()}");
            }
        }

        foreach (var attribute in element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
        {
            parts.Add($"@{attribute.Name}={attribute.Value.Trim()}");
        }

        foreach (var child in element.Elements().OrderBy(child => child.Name.ToString(), StringComparer.Ordinal))
        {
            parts.Add($"{child.Name}={child.Value.Trim()}|{string.Join('|', child.Attributes().Select(attribute => $"{attribute.Name}={attribute.Value.Trim()}"))}");
        }

        return string.Join('\n', parts);
    }
}
