using Microsoft.Extensions.Options;
using System.Text.Json;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Services.Implementations
{
    /// <summary>
    /// Google reCAPTCHA check for the login page.
    ///
    /// The browser shows the widget with SiteKey; on sign-in the SERVER posts
    /// the answer + SecretKey to Google (VerifyUrl). So the web server itself
    /// must be able to reach Google over HTTPS (directly or via ProxyUrl).
    /// Open /recaptcha-check on the server (http://localhost:…/recaptcha-check)
    /// to test that without signing in.
    /// </summary>
    public class ReCaptchaService : IReCaptchaService
    {
        private readonly HttpClient _http;
        private readonly ReCaptchaSettings _cfg;
        private readonly ILogger<ReCaptchaService> _logger;
        private readonly IWebHostEnvironment _environment;

        public ReCaptchaService(
            HttpClient http,
            IOptions<ReCaptchaSettings> opts,
            ILogger<ReCaptchaService> logger,
            IWebHostEnvironment environment)
        {
            _http = http;
            _cfg = opts.Value;
            _logger = logger;
            _environment = environment;
        }

        public async Task<bool> VerifyAsync(string token) =>
            (await CheckAsync(token)).Passed;

        public async Task<ReCaptchaResult> CheckAsync(string token)
        {
            // Development machine only. UAT ("Staging") and Production always
            // verify, whatever BypassVerification says.
            if (_cfg.BypassVerification)
            {
                if (_environment.IsDevelopment())
                {
                    _logger.LogWarning("reCAPTCHA BYPASSED in {Environment}.", _environment.EnvironmentName);
                    return new(ReCaptchaOutcome.Bypassed);
                }

                _logger.LogWarning(
                    "ReCaptcha:BypassVerification is true in {Environment} and was IGNORED.",
                    _environment.EnvironmentName);
            }

            if (string.IsNullOrWhiteSpace(_cfg.SecretKey))
                return Fail(ReCaptchaOutcome.NotConfigured, "ReCaptcha:SecretKey is empty.");

            if (string.IsNullOrWhiteSpace(token))
                return new(ReCaptchaOutcome.NoAnswer);

            string json;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["secret"] = _cfg.SecretKey,
                    ["response"] = token
                });

                using var response = await _http.PostAsync(_cfg.VerifyUrl, content, cts.Token);
                json = await response.Content.ReadAsStringAsync(cts.Token);

                if (!response.IsSuccessStatusCode)
                    return Fail(ReCaptchaOutcome.GoogleUnreachable,
                        $"{_cfg.VerifyUrl} returned HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException)
            {
                return Fail(ReCaptchaOutcome.GoogleUnreachable,
                    $"No answer from {_cfg.VerifyUrl} within 10 seconds (firewall / proxy).");
            }
            catch (HttpRequestException ex)
            {
                return Fail(ReCaptchaOutcome.GoogleUnreachable,
                    $"Cannot connect to {_cfg.VerifyUrl}: {ex.Message}");
            }

            return Read(json);
        }

        private ReCaptchaResult Read(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                var errors = root.TryGetProperty("error-codes", out var e) ? e.ToString() : "";

                if (!success)
                {
                    if (errors.Contains("invalid-input-secret") || errors.Contains("missing-input-secret"))
                        return Fail(ReCaptchaOutcome.WrongSecretKey, $"Google: {errors}");

                    _logger.LogWarning("reCAPTCHA answer rejected by Google: {Errors}", errors);
                    return new(ReCaptchaOutcome.AnswerRejected, errors);
                }

                if (_cfg.IsV3)
                {
                    var score = root.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                        ? sc.GetDouble() : 0.0;
                    if (score < _cfg.MinScore)
                    {
                        _logger.LogWarning("reCAPTCHA v3 score {Score} below {MinScore}.", score, _cfg.MinScore);
                        return new(ReCaptchaOutcome.LowScore, $"score {score}");
                    }
                }

                return new(ReCaptchaOutcome.Passed);
            }
            catch (JsonException)
            {
                return Fail(ReCaptchaOutcome.GoogleUnreachable,
                    "The answer from VerifyUrl is not Google's JSON (a proxy or firewall page?).");
            }
        }

        private ReCaptchaResult Fail(ReCaptchaOutcome outcome, string detail)
        {
            // Error level → also in "Genesis Log\Errors".
            _logger.LogError("reCAPTCHA {Outcome}: {Detail}", outcome, detail);
            return new(outcome, detail);
        }
    }
}
