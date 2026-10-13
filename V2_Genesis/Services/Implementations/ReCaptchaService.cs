using Microsoft.Extensions.Options;
using System.Text.Json;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Services.Implementations
{
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

        public async Task<bool> VerifyAsync(string token)
        {
            // ---------------------------------------------------------
            // DEVELOPMENT-ONLY BYPASS
            // Only on a developer machine (ASPNETCORE_ENVIRONMENT =
            // Development). UAT and Production ALWAYS verify with Google,
            // whatever BypassVerification says — an IIS site with a
            // non-"Production" environment name must not skip reCAPTCHA.
            // ---------------------------------------------------------
            if (_cfg.BypassVerification)
            {
                if (_environment.IsDevelopment())
                {
                    _logger.LogWarning(
                        "reCAPTCHA verification BYPASSED in {Environment}.",
                        _environment.EnvironmentName);

                    return true;
                }

                _logger.LogError(
                    "ReCaptcha:BypassVerification is true in {Environment} and was IGNORED; reCAPTCHA is verified.",
                    _environment.EnvironmentName);
            }

            if (string.IsNullOrWhiteSpace(_cfg.SecretKey))
            {
                _logger.LogError(
                    "ReCaptcha:SecretKey is not configured; sign-in is refused until it is set.");

                return false;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning(
                    "reCAPTCHA verification rejected because token was empty.");

                return false;
            }

            try
            {
                // Do not let Login wait for the default HttpClient
                // timeout of 100 seconds when Google is unreachable.
                using var cts =
                    new CancellationTokenSource(TimeSpan.FromSeconds(8));

                using var content =
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["secret"] = _cfg.SecretKey,
                            ["response"] = token
                        });

                using var response = await _http.PostAsync(
                    _cfg.VerifyUrl,
                    content,
                    cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "reCAPTCHA verification endpoint {VerifyUrl} returned HTTP {StatusCode}.",
                        _cfg.VerifyUrl,
                        (int)response.StatusCode);

                    return false;
                }

                var json =
                    await response.Content.ReadAsStringAsync(cts.Token);

                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty(
                        "success",
                        out var successElement))
                {
                    _logger.LogWarning(
                        "reCAPTCHA response did not contain a success property.");

                    return false;
                }

                var success = successElement.GetBoolean();

                if (!success)
                {
                    string? errorCodes = null;

                    if (doc.RootElement.TryGetProperty(
                            "error-codes",
                            out var errors))
                    {
                        errorCodes = errors.ToString();
                    }

                    // Error level so it is in "Genesis Log\Errors":
                    //   invalid-input-secret   → SecretKey wrong / not the pair of SiteKey
                    //   invalid-input-response → answer not valid for this key
                    //   timeout-or-duplicate   → answer expired (2 min) or used twice
                    _logger.LogError(
                        "reCAPTCHA verification was rejected by Google. Errors: {Errors}. Response: {Json}",
                        errorCodes ?? "None supplied",
                        json);

                    return false;
                }

                // v3: Google always says success for a valid token; the
                // score decides (0.0 = bot … 1.0 = human).
                if (_cfg.IsV3)
                {
                    var score =
                        doc.RootElement.TryGetProperty("score", out var s) &&
                        s.ValueKind == JsonValueKind.Number
                            ? s.GetDouble()
                            : 0.0;

                    if (score < _cfg.MinScore)
                    {
                        _logger.LogWarning(
                            "reCAPTCHA v3 score {Score} is below the minimum {MinScore}.",
                            score,
                            _cfg.MinScore);

                        return false;
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "reCAPTCHA verification timed out after 8 seconds: the server could not reach {VerifyUrl}. " +
                    "Allow outbound HTTPS to it, set ReCaptcha:ProxyUrl, or use https://www.recaptcha.net/recaptcha/api/siteverify.",
                    _cfg.VerifyUrl);

                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to connect to the reCAPTCHA verification endpoint {VerifyUrl}. " +
                    "Allow outbound HTTPS to it, set ReCaptcha:ProxyUrl, or use https://www.recaptcha.net/recaptcha/api/siteverify.",
                    _cfg.VerifyUrl);

                return false;
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "Invalid JSON received from reCAPTCHA verification.");

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "reCAPTCHA verification failed.");

                return false;
            }
        }
    }
}