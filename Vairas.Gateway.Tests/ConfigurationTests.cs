using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Vairas.Gateway.Configuration.Options;
using Vairas.Gateway.Configuration;

namespace Vairas.Gateway.Tests;

public sealed class ConfigurationTests
{
    public static (GatewayOptions Options, PublicRoute[] Routes) Load()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").AddJsonFile("routes.json").Build();
        return (configuration.GetSection("Gateway").Get<GatewayOptions>()!, configuration.GetSection("PublicRoutes").Get<PublicRoute[]>()!);
    }

    [Fact]
    public void Defaults_cover_all_owners_and_only_the_explicit_anonymous_surface()
    {
        var (options, routes) = Load();
        ProxyConfiguration.Validate(options, routes, new TestEnvironment());
        Assert.Equal(9, routes.Select(r => r.Service).Distinct().Count());
        Assert.Equal(9, routes.Count(r => r.Anonymous));
        Assert.Equal(2, routes.Count(r => r.FileTransfer));
        Assert.Single(routes, r => r.Upload);
        foreach (var route in routes)
        {
            var config = ProxyConfiguration.ToRoute(route);
            Assert.Equal(route.Anonymous ? "Anonymous" : route.Service, config.AuthorizationPolicy);
            Assert.Equal(route.FileTransfer ? "files" : route.Service == "chat" && route.Method == "POST" && route.Path.EndsWith("/messages", StringComparison.Ordinal) ? "chat" : "api", config.TimeoutPolicy);
            Assert.Equal(route.Upload ? 21 * 1024 * 1024 : route.Service == "chat" ? 65536 : (long?)null, config.MaxRequestBodySize);
        }
        foreach (var (id, service) in options.Services)
        {
            var cluster = ProxyConfiguration.ToCluster(id, service);
            Assert.Equal(service.Address, cluster.Destinations![id].Address);
            Assert.Null(cluster.HttpClient!.DangerousAcceptAnyServerCertificate);
        }
    }

    [Theory]
    [InlineData("issuer-http")][InlineData("issuer-slash")][InlineData("metadata-http")]
    [InlineData("service-missing")][InlineData("service-extra")][InlineData("audience")]
    [InlineData("address-http")][InlineData("address-path")][InlineData("address-user")][InlineData("address-query")][InlineData("address-fragment")][InlineData("address-malformed")]
    [InlineData("timeout-zero")][InlineData("timeout-huge")][InlineData("file-timeout-zero")]
    [InlineData("ip-limit-zero")][InlineData("auth-limit-zero")][InlineData("user-limit-zero")]
    [InlineData("proxy-hostname")][InlineData("forwarded-host-empty")][InlineData("forwarded-host-wildcard")]
    [InlineData("origin-wildcard")][InlineData("origin-path")][InlineData("origin-user")][InlineData("origin-nonlocal-http")]
    public void Unsafe_settings_fail_before_listening(string variant)
    {
        var (options, routes) = Load();
        switch (variant)
        {
            case "issuer-http": options.Issuer = "http://localhost:7100"; break;
            case "issuer-slash": options.Issuer += "/"; break;
            case "metadata-http": options.MetadataAddress = "http://localhost:7100/.well-known/openid-configuration"; break;
            case "service-missing": options.Services.Remove("authentication"); break;
            case "service-extra": options.Services["unknown-service"] = new(); break;
            case "audience": options.Services["user-management"].Audience = "vairas-user-management-internal"; break;
            case "address-http": options.Services["user-management"].Address = "http://service.invalid/"; break;
            case "address-path": options.Services["user-management"].Address += "internal/"; break;
            case "address-user": options.Services["user-management"].Address = "https://user:secret@localhost:7001/"; break;
            case "address-query": options.Services["user-management"].Address += "?secret=anything"; break;
            case "address-fragment": options.Services["user-management"].Address += "#fragment"; break;
            case "address-malformed": options.Services["user-management"].Address = "invalid"; break;
            case "timeout-zero": options.ApiTimeoutSeconds = 0; break;
            case "timeout-huge": options.ApiTimeoutSeconds = 601; break;
            case "file-timeout-zero": options.FileTimeoutSeconds = 0; break;
            case "ip-limit-zero": options.IpRequestsPerMinute = 0; break;
            case "auth-limit-zero": options.AuthenticationRequestsPerMinute = 0; break;
            case "user-limit-zero": options.UserRequestsPerMinute = 0; break;
            case "proxy-hostname": options.TrustedProxies = ["load-balancer"]; break;
            case "forwarded-host-empty": options.ForwardedHosts = []; break;
            case "forwarded-host-wildcard": options.ForwardedHosts = ["*"]; break;
            case "origin-wildcard": options.BrowserOrigins = ["*"]; break;
            case "origin-path": options.BrowserOrigins = ["https://web.invalid/path"]; break;
            case "origin-user": options.BrowserOrigins = ["https://user@web.invalid"]; break;
            case "origin-nonlocal-http": options.BrowserOrigins = ["http://web.invalid"]; break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Validate(options, routes, new TestEnvironment()));
    }

    [Theory]
    [InlineData("internal")][InlineData("workload-token")][InlineData("catch-all")][InlineData("method")]
    [InlineData("duplicate-id")][InlineData("duplicate-pattern")][InlineData("anonymous-business")]
    [InlineData("protected-login")][InlineData("unknown-service")][InlineData("upload-flag")][InlineData("file-flag")][InlineData("empty")]
    public void Unsafe_or_ambiguous_catalogues_fail_before_listening(string variant)
    {
        var (options, routes) = Load();
        var index = Array.FindIndex(routes, r => r.Path == "/api/v1/users/me");
        var route = routes[index];
        switch (variant)
        {
            case "internal": routes[index] = route with { Path = "/internal/v1/users" }; break;
            case "workload-token": routes[index] = route with { Path = "/connect/token" }; break;
            case "catch-all": routes[index] = route with { Path = "/api/v1/{**path}" }; break;
            case "method": routes[index] = route with { Method = "TRACE" }; break;
            case "duplicate-id": routes[index] = route with { Id = routes[0].Id }; break;
            case "duplicate-pattern": routes = [route, route with { Id = "second-owner", Service = "school-management" }]; break;
            case "anonymous-business": routes[index] = route with { Anonymous = true }; break;
            case "protected-login": index = Array.FindIndex(routes, r => r.Path == "/api/v1/auth/login"); routes[index] = routes[index] with { Anonymous = false }; break;
            case "unknown-service": routes[index] = route with { Service = "unknown-service" }; break;
            case "upload-flag": routes[index] = route with { Upload = true }; break;
            case "file-flag": routes[index] = route with { FileTransfer = true }; break;
            case "empty": routes = []; break;
        }
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Validate(options, routes, new TestEnvironment()));
    }

    [Fact]
    public void Production_requires_HTTPS_downstream_and_origins_while_test_HTTP_is_loopback_only()
    {
        var (options, routes) = Load();
        var production = new TestEnvironment { EnvironmentName = Environments.Production };
        options.BrowserOrigins = ["http://localhost:4200"];
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Validate(options, routes, production));
        options.BrowserOrigins = ["https://web.example.test"]; options.TrustedProxies = ["10.0.0.10"];
        ProxyConfiguration.Validate(options, routes, production);
        options.Services["user-management"].Address = "http://127.0.0.1:7001/";
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Validate(options, routes, production));
        ProxyConfiguration.Validate(options, routes, new TestEnvironment { EnvironmentName = "Testing" });
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Gateway.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
