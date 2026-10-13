using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using V2_Genesis.Services.Implementations;
using V2_Genesis.Services.Interfaces;
using Xunit;

namespace V2_Genesis.Tests.Services;

/// reCAPTCHA must be verified on UAT and Production; the bypass is Development only.
public class ReCaptchaServiceTests
{
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public int Calls;
        private readonly bool _success;
        public FakeGoogle(bool success) => _success = success;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_success ? "{\"success\":true}" : "{\"success\":false}")
            });
        }
    }

    private sealed class Env : IWebHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "V2_Genesis";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (ReCaptchaService svc, FakeGoogle google) Make(string env, bool bypass, bool googleSays, string secret = "secret")
    {
        var google = new FakeGoogle(googleSays);
        var svc = new ReCaptchaService(
            new HttpClient(google),
            Options.Create(new ReCaptchaSettings { SecretKey = secret, BypassVerification = bypass }),
            NullLogger<ReCaptchaService>.Instance,
            new Env(env));
        return (svc, google);
    }

    [Theory]
    [InlineData("UAT")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Bypass_is_ignored_outside_Development(string env)
    {
        var (svc, google) = Make(env, bypass: true, googleSays: false);
        Assert.False(await svc.VerifyAsync("token"));
        Assert.Equal(1, google.Calls);
    }

    [Fact]
    public async Task Empty_token_is_refused_on_UAT()
    {
        var (svc, _) = Make("UAT", bypass: true, googleSays: true);
        Assert.False(await svc.VerifyAsync(""));
    }

    [Fact]
    public async Task Bypass_works_in_Development()
    {
        var (svc, google) = Make("Development", bypass: true, googleSays: false);
        Assert.True(await svc.VerifyAsync(""));
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task Missing_secret_is_refused()
    {
        var (svc, google) = Make("Production", bypass: false, googleSays: true, secret: "");
        Assert.False(await svc.VerifyAsync("token"));
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task Google_success_is_accepted()
    {
        var (svc, _) = Make("Production", bypass: false, googleSays: true);
        Assert.True(await svc.VerifyAsync("token"));
    }

    private sealed class FakeGoogleJson : HttpMessageHandler
    {
        private readonly string _json;
        public FakeGoogleJson(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json) });
    }

    private static ReCaptchaService MakeV3(string json) => new(
        new HttpClient(new FakeGoogleJson(json)),
        Options.Create(new ReCaptchaSettings { SecretKey = "secret", Version = "v3", MinScore = 0.5 }),
        NullLogger<ReCaptchaService>.Instance,
        new Env("Production"));

    [Theory]
    [InlineData("{\"success\":true,\"score\":0.9}", true)]
    [InlineData("{\"success\":true,\"score\":0.5}", true)]
    [InlineData("{\"success\":true,\"score\":0.1}", false)]
    [InlineData("{\"success\":true}", false)]
    [InlineData("{\"success\":false,\"score\":0.9}", false)]
    public async Task V3_needs_success_and_a_score_of_at_least_MinScore(string json, bool expected)
        => Assert.Equal(expected, await MakeV3(json).VerifyAsync("token"));
}
