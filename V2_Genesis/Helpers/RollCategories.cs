namespace V2_Genesis.Helpers;

/// <summary>
/// Property categories – City of Johannesburg Rates Policy 2026/27, sections
/// 2.1, 2.2 and 5.5 (the 17 categories with ratios). This is the ONE list
/// used by every category dropdown (objection, appeal, Section 78 query /
/// review – including the multipurpose splits – Attributes and admin views).
///
/// "Farming" is no longer a category: it is "Agricultural".
/// </summary>
public static class RollCategories
{
    /// A–Z (ascending), as shown in every dropdown.
    public static readonly IReadOnlyList<string> All = new[]
    {
        "Residential",
        "Industrial",
        "Business and Commercial",
        "Agricultural",
        "Mining",
        "Public Service Purposes",
        "Public Service Infrastructure",
        "Public Benefit Organisation",
        "Multiple Purposes",
        "Municipal",
        "Private Open Space",
        "Public Open Space",
        "Private Service Infrastructure",
        "Residential with Consent Use",
        "Religious",
        "Township Development",
        "Vacant Land",
    }.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Residential Property"] = "Residential",
        ["Residential (Including Sect. Title units)"] = "Residential",
        ["Business & Commercial"] = "Business and Commercial",
        ["Business & Commercial (Including Sect. Title units)"] = "Business and Commercial",
        ["Farming"] = "Agricultural",
        ["Agriculture"] = "Agricultural",
        ["Mining Land"] = "Mining",
        ["Public Service Purpose"] = "Public Service Purposes",
        ["Public Benefit Organisations"] = "Public Benefit Organisation",
        ["MultiPurpose"] = "Multiple Purposes",
        ["MultiPurpose*"] = "Multiple Purposes",
        ["Multipurpose"] = "Multiple Purposes",
        ["Multiple Purpose"] = "Multiple Purposes",
        ["Municipal Property"] = "Municipal",
        ["Public Service Infrastructure - Private"] = "Private Service Infrastructure",
        ["Public Service Infrastructure -Private"] = "Private Service Infrastructure",
        ["Public Service Infrastructure Private"] = "Private Service Infrastructure",
        ["Consent Use"] = "Residential with Consent Use",
        ["Township Establishment"] = "Township Development",
    };

    /// The category name of the list for a roll / old value (null when unknown).
    public static string? Normalize(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrWhiteSpace(v)) return null;
        var exact = All.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return Aliases.TryGetValue(v, out var mapped) ? mapped : null;
    }
}
