using System.Security.Claims;

namespace V2_Genesis.Helpers;

/// <summary>
/// Who the admin really is (Windows user, SAP number, name, position, role).
///
/// All admin staff sign in to ONE shared account
/// (AdministrationEnquiries@Joburg.org.za). These claims belong to the
/// person, so they live ONLY in that person's own sign-in cookie – never on
/// the shared account in AspNetUserClaims. If they were stored on the shared
/// account, the last admin to log in would overwrite them, and every 30
/// minutes (when Identity re-validates the cookie) the other admins' cookies
/// would be rebuilt with that person's name and SAP number.
/// </summary>
public static class AdminLoginClaims
{
    public static readonly IReadOnlyList<string> Types = new[]
    {
        "AdminAppEmail",
        "WindowsUser",
        "SAPNumber",
        "SAPNumeric",
        "FullName",
        "Position",
        "UMRole",
        "LoginType",
    };

    /// The claim types that were (wrongly) stored on the shared account.
    public static readonly IReadOnlyList<string> SharedAccountTypes = new[]
    {
        "SAPNumber", "FullName", "Position", "UMRole",
    };

    public static bool IsPersonal(string claimType) =>
        Types.Contains(claimType, StringComparer.Ordinal);

    /// <summary>
    /// When Identity rebuilds a cookie (security-stamp check), keep THIS
    /// person's own admin claims and drop any copy that came from the
    /// shared account in the database.
    /// </summary>
    public static void CopyTo(ClaimsPrincipal? current, ClaimsPrincipal? rebuilt)
    {
        if (current is null || rebuilt?.Identity is not ClaimsIdentity target)
            return;

        var own = current.Claims.Where(c => IsPersonal(c.Type)).ToList();
        if (own.Count == 0)
            return; // a client – nothing to keep

        foreach (var stale in target.Claims.Where(c => IsPersonal(c.Type)).ToList())
            target.TryRemoveClaim(stale);

        // One value per type: the first one this person's cookie holds.
        foreach (var claim in own.GroupBy(c => c.Type).Select(g => g.First()))
            target.AddClaim(new Claim(claim.Type, claim.Value, claim.ValueType));
    }
}
