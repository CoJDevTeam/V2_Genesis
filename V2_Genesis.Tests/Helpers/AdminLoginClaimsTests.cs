using System.Security.Claims;
using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

public class AdminLoginClaimsTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Identity.Application"));

    [Fact]
    public void Refresh_keeps_this_admins_own_identity_not_the_last_admin_to_log_in()
    {
        // Admin A's cookie.
        var current = Principal(
            (ClaimTypes.Name, "AdministrationEnquiries@Joburg.org.za"),
            ("WindowsUser", @"JOBURG\11111111"),
            ("SAPNumber", @"JOBURG\11111111"),
            ("SAPNumeric", "11111111"),
            ("FullName", "Admin A"),
            ("Position", "Valuer"),
            ("UMRole", "Admin"),
            ("LoginType", "AdminWindows"));

        // Rebuilt from the shared account, which an old build overwrote with admin B.
        var rebuilt = Principal(
            (ClaimTypes.Name, "AdministrationEnquiries@Joburg.org.za"),
            ("SAPNumber", @"JOBURG\22222222"),
            ("FullName", "Admin B"),
            ("Position", "Clerk"),
            ("UMRole", "Admin"));

        AdminLoginClaims.CopyTo(current, rebuilt);

        Assert.Equal(@"JOBURG\11111111", rebuilt.FindFirstValue("SAPNumber"));
        Assert.Equal("11111111", rebuilt.FindFirstValue("SAPNumeric"));
        Assert.Equal(@"JOBURG\11111111", rebuilt.FindFirstValue("WindowsUser"));
        Assert.Equal("Admin A", rebuilt.FindFirstValue("FullName"));
        Assert.Equal("Valuer", rebuilt.FindFirstValue("Position"));
        Assert.Single(rebuilt.FindAll("FullName"));
        Assert.Single(rebuilt.FindAll("SAPNumber"));
        Assert.Equal("AdministrationEnquiries@Joburg.org.za", rebuilt.FindFirstValue(ClaimTypes.Name));
    }

    [Fact]
    public void Client_cookie_is_left_as_rebuilt()
    {
        var current = Principal((ClaimTypes.Name, "client@example.com"));
        var rebuilt = Principal((ClaimTypes.Name, "client@example.com"), ("FullName", "Client"));

        AdminLoginClaims.CopyTo(current, rebuilt);

        Assert.Equal("Client", rebuilt.FindFirstValue("FullName"));
    }

    [Fact]
    public void Null_principals_do_nothing()
    {
        AdminLoginClaims.CopyTo(null, null);
        AdminLoginClaims.CopyTo(Principal(("FullName", "A")), null);
    }
}
