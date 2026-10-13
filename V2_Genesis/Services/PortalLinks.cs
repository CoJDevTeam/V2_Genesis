namespace V2_Genesis.Services;

/// <summary>
/// The two addresses of Genesis and the links in e-mails.
///
///   External  https://objections.joburg.org.za   – clients (internet)
///   Internal  http://168.89.201.36:75            – City staff (City network)
///
/// appsettings.json → "Portal": { "ExternalUrl": "...", "InternalUrl": "..." }
///
/// <see cref="Current"/> is the address the app is being used on right now
/// (read from the request), but only when it is one of the two configured
/// addresses – a request with any other Host header falls back to External,
/// so a link in an e-mail can never point to a host an outsider chose.
/// </summary>
public interface IPortalLinks
{
    string External { get; }
    string Internal { get; }

    /// Address of the current request (External or Internal); External when there is no request.
    string Current { get; }

    /// Link for clients (external address).
    string Client(string pathAndQuery);

    /// Link for City staff (internal address).
    string Staff(string pathAndQuery);
}

public sealed class PortalLinks : IPortalLinks
{
    public const string DefaultExternal = "https://objections.joburg.org.za";
    public const string DefaultInternal = "http://168.89.201.36:75";

    private readonly IHttpContextAccessor _http;

    public PortalLinks(IConfiguration config, IHttpContextAccessor http)
    {
        _http = http;
        External = Clean(config["Portal:ExternalUrl"]) ?? Clean(config["Portal:BaseUrl"]) ?? DefaultExternal;
        Internal = Clean(config["Portal:InternalUrl"]) ?? DefaultInternal;
    }

    public string External { get; }
    public string Internal { get; }

    public string Current => Resolve(_http.HttpContext?.Request, External, Internal);

    public string Client(string pathAndQuery) => Join(External, pathAndQuery);
    public string Staff(string pathAndQuery) => Join(Internal, pathAndQuery);

    /// Pure rule (unit-tested): the request's own base address when it is one
    /// of the configured addresses, otherwise External.
    public static string Resolve(HttpRequest? request, string external, string internalUrl)
    {
        if (request is null || !request.Host.HasValue)
            return external;

        var current = Clean($"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}") ?? external;

        if (SameOrigin(current, internalUrl)) return internalUrl;
        if (SameOrigin(current, external)) return external;
        return external;
    }

    public static string Join(string baseUrl, string pathAndQuery)
    {
        var path = pathAndQuery ?? string.Empty;
        if (path.Length > 0 && !path.StartsWith('/')) path = "/" + path;
        return baseUrl.TrimEnd('/') + path;
    }

    private static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) &&
        Uri.TryCreate(b, UriKind.Absolute, out var y) &&
        string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) &&
        x.Port == y.Port;

    private static string? Clean(string? url)
    {
        var v = url?.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
