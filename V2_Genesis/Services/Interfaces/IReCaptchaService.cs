namespace V2_Genesis.Services.Interfaces
{
    public interface IReCaptchaService
    {
        /// True when the sign-in may continue.
        Task<bool> VerifyAsync(string token);

        /// Same check, with the reason (used for the message on the login
        /// page and by /recaptcha-check on the server).
        Task<ReCaptchaResult> CheckAsync(string token);
    }

    public enum ReCaptchaOutcome
    {
        Passed,
        Bypassed,          // Development only
        NoAnswer,          // the box was not ticked
        AnswerRejected,    // expired (2 min), used twice, or not for this key
        LowScore,          // v3 only
        WrongSecretKey,    // SecretKey is not the pair of SiteKey
        NotConfigured,     // SecretKey empty
        GoogleUnreachable  // the server cannot reach Google (firewall / proxy)
    }

    public sealed record ReCaptchaResult(ReCaptchaOutcome Outcome, string Detail = "")
    {
        public bool Passed => Outcome is ReCaptchaOutcome.Passed or ReCaptchaOutcome.Bypassed;

        /// Message for the person signing in.
        public string UserMessage => Outcome switch
        {
            ReCaptchaOutcome.NoAnswer =>
                "Please tick \"I'm not a robot\" before signing in.",
            ReCaptchaOutcome.AnswerRejected or ReCaptchaOutcome.LowScore =>
                "The reCAPTCHA expired or could not be confirmed. Please tick \"I'm not a robot\" again and sign in.",
            _ =>
                "Sign-in is temporarily unavailable because the security check (reCAPTCHA) could not be completed. Please try again later or contact support."
        };
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
