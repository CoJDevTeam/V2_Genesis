using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Controllers;

/// <summary>
/// GET /recaptcha-check – opened ON THE WEB SERVER ITSELF
/// (http://localhost:&lt;port&gt;/recaptcha-check). Tells whether this server can
/// reach Google and whether its SecretKey is valid, without signing in.
/// Refused from any other computer; never shows the keys.
/// </summary>
[AllowAnonymous]
public class ReCaptchaCheckController : Controller
{
    private readonly IReCaptchaService _captcha;
    private readonly ReCaptchaSettings _cfg;

    public ReCaptchaCheckController(IReCaptchaService captcha, IOptions<ReCaptchaSettings> cfg)
    {
        _captcha = captcha;
        _cfg = cfg.Value;
    }

    [HttpGet("recaptcha-check")]
    public async Task<IActionResult> Index()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null || !IPAddress.IsLoopback(ip))
            return NotFound();

        // A made-up answer: Google replies "invalid-input-response" when it was
        // reached AND the secret is valid, "invalid-input-secret" when not.
        var r = await _captcha.CheckAsync("genesis-connection-test");

        var verdict = r.Outcome switch
        {
            ReCaptchaOutcome.AnswerRejected =>
                "OK – this server reached Google and the SecretKey is valid. reCAPTCHA sign-in will work.",
            ReCaptchaOutcome.WrongSecretKey =>
                "PROBLEM – Google was reached but the SecretKey is wrong (not the pair of the SiteKey).",
            ReCaptchaOutcome.GoogleUnreachable =>
                "PROBLEM – this server cannot reach Google. Open outbound HTTPS to the VerifyUrl host, set ReCaptcha:ProxyUrl, or try https://www.recaptcha.net/recaptcha/api/siteverify.",
            ReCaptchaOutcome.UnreachableAllowed =>
                "PROBLEM – this server cannot reach Google. Sign-in is TEMPORARILY allowed (AllowSignInWhenGoogleUnreachable = true). Fix the firewall/proxy, then set it back to false.",
            ReCaptchaOutcome.NotConfigured =>
                "PROBLEM – ReCaptcha:SecretKey is empty.",
            ReCaptchaOutcome.Bypassed =>
                "Development: verification is bypassed.",
            _ => $"Result: {r.Outcome}"
        };

        static string Mask(string? v) =>
            string.IsNullOrWhiteSpace(v) ? "(empty)" : v.Length <= 8 ? "****" : v[..8] + "…";

        var text =
            $"{verdict}\n\n" +
            $"Outcome      : {r.Outcome}\n" +
            $"Detail       : {r.Detail}\n" +
            $"Environment  : {HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName}\n" +
            $"SiteKey      : {Mask(_cfg.SiteKey)}\n" +
            $"SecretKey    : {Mask(_cfg.SecretKey)}\n" +
            $"Version      : {_cfg.Version}\n" +
            $"VerifyUrl    : {_cfg.VerifyUrl}\n" +
            $"ProxyUrl     : {(string.IsNullOrWhiteSpace(_cfg.ProxyUrl) ? "(none)" : _cfg.ProxyUrl)}\n" +
            $"AllowSignInWhenGoogleUnreachable : {_cfg.AllowSignInWhenGoogleUnreachable}\n";

        return Content(text, "text/plain");
    }
}
