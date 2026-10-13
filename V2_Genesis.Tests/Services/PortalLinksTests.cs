using Microsoft.AspNetCore.Http;
using V2_Genesis.Services;
using Xunit;

namespace V2_Genesis.Tests.Services;

/// External link for clients, internal link for staff; the "current" address
/// is read from the request but only when it is one of the two.
public class PortalLinksTests
{
    private const string Ext = "https://objections.joburg.org.za";
    private const string Int = "http://168.89.201.36:75";

    private static HttpRequest Req(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        return ctx.Request;
    }

    [Fact] public void Internal_request_gives_internal() =>
        Assert.Equal(Int, PortalLinks.Resolve(Req("http", "168.89.201.36:75"), Ext, Int));

    [Fact] public void External_request_gives_external() =>
        Assert.Equal(Ext, PortalLinks.Resolve(Req("https", "objections.joburg.org.za"), Ext, Int));

    [Fact] public void Unknown_host_falls_back_to_external() =>
        Assert.Equal(Ext, PortalLinks.Resolve(Req("https", "evil.example.com"), Ext, Int));

    [Fact] public void No_request_gives_external() =>
        Assert.Equal(Ext, PortalLinks.Resolve(null, Ext, Int));

    [Theory]
    [InlineData("https://objections.joburg.org.za/", "/evidence/verify?objectionNo=1", "https://objections.joburg.org.za/evidence/verify?objectionNo=1")]
    [InlineData("http://168.89.201.36:75", "evidence/verify", "http://168.89.201.36:75/evidence/verify")]
    public void Join_builds_one_slash(string b, string p, string expected) =>
        Assert.Equal(expected, PortalLinks.Join(b, p));
}
