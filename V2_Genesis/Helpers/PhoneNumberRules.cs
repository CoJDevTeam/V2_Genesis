using System.Text.RegularExpressions;

namespace V2_Genesis.Helpers;

/// <summary>
/// South African phone numbers on the forms.
///
/// Accepted (spaces, dashes, brackets and dots are ignored):
///   0XX XXX XXXX      – any number starting with 0, 10 digits (067…, 082…, 011…)
///   +27 XX XXX XXXX   – international form, +27 then 9 digits (no leading 0)
///   27XX XXX XXXX     – same without the +
///   +27 0XX XXX XXXX  – people often keep the 0 after +27; accepted too
/// </summary>
public static class PhoneNumberRules
{
    public const string Hint =
        "Enter a South African number starting with 0 (e.g. 0671234567) or +27 (e.g. +27671234567).";

    private static readonly Regex Separators = new(@"[\s\-\(\)\.]", RegexOptions.Compiled);

    /// Removes spaces, dashes, brackets and dots; keeps digits and a leading +.
    public static string Compact(string? value) =>
        Separators.Replace(value?.Trim() ?? string.Empty, string.Empty);

    public static bool IsValid(string? value)
    {
        var v = Compact(value);
        if (v.Length == 0) return false;

        if (v.StartsWith("+27")) v = v[3..];
        else if (v.StartsWith("27") && v.Length == 11) v = v[2..];
        else if (v.StartsWith("0")) return v.Length == 10 && v.All(char.IsDigit);
        else return false;

        // After the country code: 9 digits, or 10 with the 0 kept.
        if (v.StartsWith("0")) v = v[1..];
        return v.Length == 9 && v.All(char.IsDigit) && v[0] != '0';
    }
}
