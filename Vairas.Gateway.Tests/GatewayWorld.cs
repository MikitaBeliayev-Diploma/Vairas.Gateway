using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Vairas.Gateway.Configuration.Options;
using Vairas.Gateway.Configuration;

namespace Vairas.Gateway.Tests;

public sealed class GatewayWorld : IAsyncLifetime
{
    private readonly List<WebApplication> owners = [];
    private readonly List<RSA> keys = [RSA.Create(2048)];
    private readonly X509Certificate2 certificate = CreateCertificate();
    private WebApplication issuerApp = null!;
    public Dictionary<string, string> Addresses { get; } = [];
    public ConcurrentQueue<string> Cancelled { get; } = new();
    public string Issuer { get; private set; } = "";
    public PublicRoute[] Routes { get; } = JsonSerializer.Deserialize<Catalogue>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "routes.json")))!.PublicRoutes;
    public Guid Identity { get; } = Guid.NewGuid();
    public static readonly byte[] Download = "%PDF-1.7\nSynthetic document bytes\n"u8.ToArray();

    public async Task InitializeAsync()
    {
        var issuerBuilder = WebApplication.CreateBuilder();
        issuerBuilder.Configuration["AllowedHosts"] = "127.0.0.1";
        issuerBuilder.Logging.ClearProviders();
        issuerBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        issuerApp = issuerBuilder.Build();
        issuerApp.MapGet("/.well-known/openid-configuration", () => Results.Json(new { issuer = Issuer, jwks_uri = Issuer + "/.well-known/jwks.json" }));
        issuerApp.MapGet("/.well-known/jwks.json", () => Results.Json(new { keys = keys.Select(key =>
        {
            var p = key.ExportParameters(false);
            return new { kty = "RSA", use = "sig", alg = "RS256", kid = Key(key).KeyId, n = Base64UrlEncoder.Encode(p.Modulus!), e = Base64UrlEncoder.Encode(p.Exponent!) };
        }).ToArray() }));
        await issuerApp.StartAsync();
        Issuer = Address(issuerApp);
        foreach (var service in ProxyConfiguration.ServiceNames)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["AllowedHosts"] = "127.0.0.1";
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var owner = builder.Build();
            owner.Run(http => HandleOwnerAsync(service, http));
            await owner.StartAsync(); owners.Add(owner); Addresses[service] = Address(owner) + "/";
        }
    }

    public async Task<GatewayHandle> StartGatewayAsync(Dictionary<string, string?>? overrides = null, string environment = "Development", CaptureLogs? logs = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "127.0.0.1",
            ["Gateway:Issuer"] = Issuer, ["Gateway:MetadataAddress"] = Issuer + "/.well-known/openid-configuration",
            ["Gateway:IpRequestsPerMinute"] = "10000", ["Gateway:AuthenticationRequestsPerMinute"] = "10000", ["Gateway:UserRequestsPerMinute"] = "10000"
        };
        foreach (var pair in Addresses) configuration[$"Gateway:Services:{pair.Key}:Address"] = pair.Value;
        if (overrides is not null) foreach (var pair in overrides) configuration[pair.Key] = pair.Value;
        var app = GatewayApplication.Build(["--environment", environment, "--urls", "http://127.0.0.1:0"], builder =>
        {
            builder.Configuration.AddInMemoryCollection(configuration);
            foreach (var service in ProxyConfiguration.ServiceNames)
                builder.Services.Configure<JwtBearerOptions>(service, options => options.BackchannelHttpHandler = new HttpClientHandler
                {
                    // Only the ephemeral fixture certificate is trusted; production code has no bypass.
                    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint
                });
        });
        if (logs is not null) app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(15) });
    }

    public string Token(string audience = "vairas-user-management", Guid? identity = null, string? subject = null, string? session = null, string? issuer = null,
        DateTime? expires = null, string algorithm = SecurityAlgorithms.RsaSha256, bool noSession = false, bool noExpiry = false, bool duplicateSubject = false, RSA? key = null)
    {
        var claims = new List<Claim> { new("sub", subject ?? (identity ?? Identity).ToString()) };
        if (!noSession) claims.Add(new("sid", session ?? Guid.NewGuid().ToString()));
        if (duplicateSubject) claims.Add(new("sub", Guid.NewGuid().ToString()));
        var token = new JwtSecurityToken(issuer ?? Issuer, audience, claims, notBefore: DateTime.UtcNow.AddHours(-2),
            expires: noExpiry ? null : expires ?? DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(Key(key ?? keys[0]), algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public RSA RotateKey()
    {
        var key = RSA.Create(2048); keys.Add(key); return key;
    }

    private static RsaSecurityKey Key(RSA key) => new(key) { KeyId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) };

    private async Task HandleOwnerAsync(string service, HttpContext http)
    {
        http.Response.Headers["X-Upstream"] = service;
        var behavior = http.Request.Query["simulate"].ToString();
        if (behavior is "slow" or "cancel")
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), http.RequestAborted); }
            catch (OperationCanceledException) { Cancelled.Enqueue(behavior); return; }
        }
        if (behavior == "problem")
        {
            http.Response.StatusCode = 409;
            http.Response.Headers["X-Correlation-ID"] = "owner-value";
            http.Response.ContentType = "application/problem+json";
            await http.Response.WriteAsync("{\"status\":409,\"code\":\"OWNER_CONFLICT\",\"detail\":\"Unchanged owner response\"}"); return;
        }
        if (behavior == "empty") { http.Response.StatusCode = 409; http.Response.ContentLength = 0; return; }
        if (behavior == "cookies")
        {
            http.Response.Headers.Append("Set-Cookie", "oidc=opaque; path=/; secure; httponly; samesite=lax");
            http.Response.Headers.Location = "https://localhost:7000/api/v1/users/me";
        }
        if (behavior == "cors")
        {
            http.Response.Headers.AccessControlAllowOrigin = "https://untrusted.invalid";
            http.Response.Headers.AccessControlAllowCredentials = "true";
        }
        if (http.Request.Path.StartsWithSegments("/api/v1/document-downloads"))
        {
            http.Response.Headers.ContentDisposition = "attachment; filename=synthetic.pdf";
            http.Response.ContentType = "application/pdf";
            await http.Response.Body.WriteAsync(Download); return;
        }
        using var bytes = new MemoryStream();
        await http.Request.Body.CopyToAsync(bytes, http.RequestAborted);
        if (http.Request.Path.Value?.EndsWith("/document-files", StringComparison.Ordinal) == true)
        {
            await http.Response.WriteAsJsonAsync(new { size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes.ToArray())), contentType = http.Request.ContentType }); return;
        }
        await http.Response.WriteAsJsonAsync(new
        {
            service, method = http.Request.Method, path = http.Request.Path.Value, query = http.Request.QueryString.Value,
            headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body = Convert.ToBase64String(bytes.ToArray())
        });
    }

    public static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Gateway fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // Schannel requires an imported key container for a TLS server on Windows.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public async Task DisposeAsync()
    {
        foreach (var owner in owners) await owner.DisposeAsync();
        await issuerApp.DisposeAsync(); certificate.Dispose(); foreach (var key in keys) key.Dispose();
    }

    private sealed record Catalogue(PublicRoute[] PublicRoutes);
}
