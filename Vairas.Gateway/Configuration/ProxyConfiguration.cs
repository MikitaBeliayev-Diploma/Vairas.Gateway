using System.Net;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;


public static partial class ProxyConfiguration
{
    public static readonly string[] ServiceNames = ["authentication", "user-management", "school-management", "scheduling", "vehicle-management", "learning-tracking", "notifications", "document-management", "chat"];
    private static readonly HashSet<string> AnonymousOperations = new(StringComparer.Ordinal)
    {
        "POST /api/v1/auth/registrations", "POST /api/v1/auth/login", "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/challenges", "POST /api/v1/auth/challenges/completion",
        "POST /api/v1/auth/oidc/requests", "POST /api/v1/auth/oidc/callback",
        "GET /.well-known/openid-configuration", "GET /.well-known/jwks.json"
    };

    public static void Validate(GatewayOptions options, IReadOnlyList<PublicRoute> routes, IHostEnvironment environment)
    {
        if (!Https(options.Issuer) || options.Issuer.EndsWith('/') || !Https(options.MetadataAddress))
            throw new InvalidOperationException("An HTTPS issuer and metadata address are required.");
        if (!options.Services.Keys.Order().SequenceEqual(ServiceNames.Order()))
            throw new InvalidOperationException("Configure exactly the nine supported services.");
        foreach (var (id, service) in options.Services)
        {
            if (service.Audience != "vairas-" + id || !Uri.TryCreate(service.Address, UriKind.Absolute, out var address) ||
                !string.IsNullOrEmpty(address.UserInfo) || address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment) ||
                address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback && (environment.IsDevelopment() || environment.IsEnvironment("Testing"))))
                throw new InvalidOperationException($"Invalid destination or user audience for {id}.");
        }
        if (options.ApiTimeoutSeconds is < 1 or > 600 || options.FileTimeoutSeconds is < 1 or > 600 ||
            options.IpRequestsPerMinute is < 1 or > 100000 || options.AuthenticationRequestsPerMinute is < 1 or > 100000 || options.UserRequestsPerMinute is < 1 or > 100000)
            throw new InvalidOperationException("Configure bounded positive timeouts and rate limits.");
        if (options.TrustedProxies.Any(p => !IPAddress.TryParse(p, out _)) ||
            options.ForwardedHosts.Length == 0 || options.ForwardedHosts.Any(h => string.IsNullOrWhiteSpace(h) || h.Contains('*') || h.Contains('/') || h.Contains(':')))
            throw new InvalidOperationException("Configure explicit proxy IP addresses and forwarded hosts.");
        foreach (var origin in options.BrowserOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback && environment.IsDevelopment()))
                throw new InvalidOperationException("Browser origins must be exact HTTPS origins (loopback HTTP in Development only).");
        if (routes.Count == 0 || routes.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count ||
            routes.Select(r => r.Method + " " + Parameters().Replace(r.Path, "{}")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
            throw new InvalidOperationException("Public routes must be nonempty and unambiguous.");
        foreach (var route in routes)
        {
            var key = route.Method + " " + route.Path;
            var metadata = route.Service == "authentication" && route.Method == "GET" && route.Path is "/.well-known/openid-configuration" or "/.well-known/jwks.json";
            if (string.IsNullOrWhiteSpace(route.Id) || !options.Services.ContainsKey(route.Service) || route.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE") ||
                !route.Path.StartsWith("/api/v1/", StringComparison.Ordinal) && !metadata || route.Path.IndexOfAny(['*', '?', '#', '\\']) >= 0 || route.Path.Contains("//") || route.Path.Contains("..") ||
                route.Anonymous != (route.Service == "authentication" && AnonymousOperations.Contains(key)) ||
                route.Upload != (route.Service == "document-management" && route.Method == "POST" && route.Path == "/api/v1/schools/{schoolId}/document-files") ||
                route.FileTransfer != (route.Upload || route.Service == "document-management" && route.Method == "GET" && route.Path == "/api/v1/document-downloads/{grantId}"))
                throw new InvalidOperationException($"Invalid public route {route.Id}.");
        }
    }

    public static RouteConfig ToRoute(PublicRoute route) => new()
    {
        RouteId = route.Id,
        ClusterId = route.Service,
        Match = new RouteMatch { Path = route.Path, Methods = [route.Method] },
        AuthorizationPolicy = route.Anonymous ? "Anonymous" : route.Service,
        RateLimiterPolicy = route.Anonymous ? route.Path.StartsWith("/api/", StringComparison.Ordinal) ? "authentication" : null : "user",
        TimeoutPolicy = route.FileTransfer ? "files" : route.Service == "chat" && route.Method == "POST" && route.Path.EndsWith("/messages", StringComparison.Ordinal) ? "chat" : "api",
        MaxRequestBodySize = route.Upload ? 21 * 1024 * 1024 : route.Service == "chat" ? 65536 : null
    };

    public static ClusterConfig ToCluster(string id, ServiceDestination service) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig> { [id] = new() { Address = service.Address } },
        HttpClient = new HttpClientConfig { EnableMultipleHttp2Connections = true },
        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(120), AllowResponseBuffering = false }
    };

    private static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    [GeneratedRegex("\\{[^}]+\\}")]
    private static partial Regex Parameters();
}
