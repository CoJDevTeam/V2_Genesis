using System.Globalization;

namespace V2_Genesis.Helpers;

/// <summary>
/// Writes a roll "With Effective Date" (WefDate) in full, e.g. "01 July 2023".
///
/// Two kinds of text reach this helper:
///  • "MM/dd/yyyy HH:mm:ss" (e.g. "07/01/2023 00:00:00", "08/28/2025 00:00:00"):
///    a SQL date/datetime column that Dapper put into a string property. Dapper
///    converts with the invariant culture, which is MONTH-first, so
///    07/01/2023 is 1 July 2023.
///  • A date typed as text in the roll without a time ("10/01/2025"): read
///    day-first (South African), so 10 January 2025.
/// Returns "" for an empty / NULL value and the original text if it is not a date.
/// </summary>
public static class WefDateFormatter
{
    private static readonly CultureInfo ZA = CultureInfo.GetCultureInfo("en-ZA");

    private static readonly System.Text.RegularExpressions.Regex InvariantDateTime =
        new(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}$");

    private static readonly string[] Formats =
    {
        "dd/MM/yyyy HH:mm:ss", "d/M/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss",
        "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm",
        "dd/MM/yyyy", "d/M/yyyy",
        "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd",
        "yyyyMMdd"
    };

    public static string Format(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        // Dapper's invariant DateTime → string ("MM/dd/yyyy HH:mm:ss").
        if (InvariantDateTime.IsMatch(raw) &&
            DateTime.TryParseExact(raw, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var inv))
        {
            return inv.ToString("dd MMMM yyyy", ZA);
        }

        if (DateTime.TryParseExact(raw, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ||
            DateTime.TryParse(raw, ZA, DateTimeStyles.AllowWhiteSpaces, out d))
        {
            return d.ToString("dd MMMM yyyy", ZA);
        }

        return raw;
    }

    public static string Format(DateTime? value) =>
        value.HasValue ? value.Value.ToString("dd MMMM yyyy", ZA) : string.Empty;
}
