using System.Globalization;

namespace V2_Genesis.Helpers;

/// One roll row's lease flag: unit type FL or SL = lease.
public sealed class LeaseFlag
{
    public long? UnitKey { get; set; }
    public long? ValuationKey { get; set; }
    public bool IsLease { get; set; }
}

/// <summary>
/// LEASE (Y/N) column on the search results and the Lease field on
/// Property Details: Y / Yes when the unit type is FL or SL, else N / No.
/// </summary>
public static class LeaseFlags
{
    public static string SearchText(LeaseFlag f) => f.IsLease ? "Y" : "N";

    public static string DetailText(LeaseFlag f) => f.IsLease ? "Yes" : "No";

    /// "1969281", "1969281.0", "1.969281E+06" → 1969281.
    public static long? Key(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d >= long.MinValue && d <= long.MaxValue)
            return (long)Math.Round(d);
        return null;
    }

    /// The flag for this unit + valuation; the unit alone when the valuation does not match.
    public static LeaseFlag? Find(IReadOnlyList<LeaseFlag> flags, string? unitKey, string? valuationKey)
    {
        var u = Key(unitKey);
        if (u is null) return null;
        var v = Key(valuationKey);

        var forUnit = flags.Where(f => f.UnitKey == u).ToList();
        if (forUnit.Count == 0) return null;

        return (v is null ? null : forUnit.FirstOrDefault(f => f.ValuationKey == v))
               ?? forUnit[0];
    }
}
