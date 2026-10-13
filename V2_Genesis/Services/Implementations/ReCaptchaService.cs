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
                    _logger.LogWarning(
                        "reCAPTCHA verification endpoint returned HTTP {StatusCode}.",
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

                    _logger.LogWarning(
                        "reCAPTCHA verification was rejected by Google. Errors: {Errors}",
                        errorCodes ?? "None supplied");
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "reCAPTCHA verification timed out after 8 seconds.");

                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to connect to the reCAPTCHA verification endpoint.");

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