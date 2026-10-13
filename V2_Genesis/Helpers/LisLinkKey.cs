namespace V2_Genesis.Helpers;

/// <summary>
/// Key saved in LinkedProperties.IDProperty for a property linked from LIS.
///
/// LIS units of one sectional-title scheme share the same Property ID (e.g. 43
/// units of HYDE PARK EXT.89 ERF 427 all have 1848896), so linking by Property
/// ID showed – and later unlinked – the whole scheme. A LIS link now stores
/// the ONE unit the client chose: "{UnitKey}:{ValuationKey}".
/// dbo.DashboardLinked reads the same format (older links that hold only a
/// Property ID still work and show one row).
/// </summary>
public static class LisLinkKey
{
    public static string? Build(string? unitKey, string? valuationKey)
    {
        var u = unitKey?.Trim();
        var v = valuationKey?.Trim();
        return string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(v) || u == "0" || v == "0"
            ? null
            : $"{u}:{v}";
    }
}
