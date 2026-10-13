using System.Text.RegularExpressions;

namespace V2_Genesis.Helpers;

/// <summary>
/// "Natural" A–Z order for township, scheme and other names: numbers are
/// compared as numbers, so
///   ALLANDALE EXT.9, ALLANDALE EXT.49   (not EXT.49 before EXT.9)
///   AMOROSA EXT.10, AMOROSA EXT.11, AMOROSA EXT.30
/// Case-insensitive; leading/trailing spaces ignored.
/// </summary>
public sealed class NaturalSort : IComparer<string?>
{
    public static readonly NaturalSort Instance = new();

    private static readonly Regex Parts = new(@"\d+|\D+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var a = Parts.Matches(x.Trim());
        var b = Parts.Matches(y.Trim());

        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var pa = a[i].Value;
            var pb = b[i].Value;
            var na = char.IsDigit(pa[0]);
            var nb = char.IsDigit(pb[0]);

            int c;
            if (na && nb)
            {
                var ta = pa.TrimStart('0');
                var tb = pb.TrimStart('0');
                c = ta.Length != tb.Length
                    ? ta.Length.CompareTo(tb.Length)
                    : string.CompareOrdinal(ta, tb);
                if (c == 0) c = pa.Length.CompareTo(pb.Length);
            }
            else
            {
                c = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            }

            if (c != 0) return c;
        }

        return a.Count.CompareTo(b.Count);
    }

    /// Sorted copy, without blanks and duplicates (case-insensitive).
    public static List<string> Order(IEnumerable<string?>? values) =>
        (values ?? Enumerable.Empty<string?>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, Instance)
            .ToList();
}
