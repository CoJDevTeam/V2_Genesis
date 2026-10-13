namespace V2_Genesis.Services.Interfaces
{
    public interface IReCaptchaService
    {
        Task<bool> VerifyAsync(string token);
    }
    public class ReCaptchaSettings
    {
        public string SiteKey { get; set; } = string.Empty;

        public string SecretKey { get; set; } = string.Empty;

        public string VerifyUrl { get; set; } =
            "https://www.google.com/recaptcha/api/siteverify";

        public bool BypassVerification { get; set; }

        /// Optional web proxy for the call to Google, e.g.
        /// "http://proxy.joburg.org.za:8080", when the server can only reach
        /// the internet through a proxy. Empty = the server's default.
        public string? ProxyUrl { get; set; }

        /// "v2" = "I'm not a robot" checkbox (default).
        /// "v3" = invisible score key. Must match the TYPE of the key in the
        /// Google reCAPTCHA admin console – a mismatch shows
        /// "ERROR for site owner: Invalid key type".
        public string Version { get; set; } = "v2";

        /// v3 only: lowest score accepted (0.0 = bot … 1.0 = human).
        public double MinScore { get; set; } = 0.5;

        public bool IsV3 =>
            string.Equals(Version?.Trim(), "v3", StringComparison.OrdinalIgnoreCase);
    }

}
