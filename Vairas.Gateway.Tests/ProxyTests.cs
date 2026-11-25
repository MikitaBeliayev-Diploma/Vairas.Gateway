using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Vairas.Gateway.Tests;

public sealed class ProxyTests(GatewayWorld world) : IClassFixture<GatewayWorld>
{
    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Every_public_operation_routes_to_its_owner_over_real_HTTP()
    {
        await using var gateway = await world.StartGatewayAsync();
        Assert.NotEmpty(world.Routes);
        foreach (var route in world.Routes)
        {
            var path = Regex.Replace(route.Path, "\\{[^}]+\\}", _ => Guid.NewGuid().ToString());
            using var request = Request(new(route.Method), path, route.Anonymous ? null : world.Token("vairas-" + route.Service));
            using var response = await gateway.Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"{route.Id}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            Assert.Equal(route.Service, response.Headers.GetValues("X-Upstream").Single());
        }
    }

    [Theory]
    [InlineData("/internal/v1/sessions/validate")]
    [InlineData("/internal/v1/delegated/documents")]
    [InlineData("/connect/token")]
    [InlineData("/api/v1/chat/unpublished-operation")]
    [InlineData("/mcp")]
    [InlineData("/api/v1/schools/anything/unpublished-operation")]
    public async Task Private_and_unknown_paths_are_never_forwarded(string path)
    {
        await using var gateway = await world.StartGatewayAsync();
        using var response = await gateway.Client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Upstream"));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wrong_method_returns_405_and_does_not_reach_owner()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var response = await gateway.Client.DeleteAsync("/api/v1/users/me");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Upstream"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    [InlineData("workload")]
    [InlineData("subject")]
    [InlineData("empty-subject")]
    [InlineData("session")]
    [InlineData("empty-session")]
    [InlineData("no-session")]
    [InlineData("issuer")]
    [InlineData("unsigned")]
    [InlineData("signature")]
    [InlineData("duplicate-subject")]
    [InlineData("no-expiry")]
    [InlineData("algorithm")]
    public async Task Invalid_user_tokens_are_rejected_before_proxying(string invalid)
    {
        await using var gateway = await world.StartGatewayAsync();
        using var otherKey = RSA.Create(2048);
        var token = invalid switch
        {
            "missing" => null, "expired" => world.Token(expires: DateTime.UtcNow.AddMinutes(-1)),
            "wrong-audience" => world.Token("vairas-school-management"), "workload" => world.Token("vairas-user-management-internal", subject: "notifications", noSession: true),
            "subject" => world.Token(subject: "invalid"), "empty-subject" => world.Token(subject: Guid.Empty.ToString()),
            "session" => world.Token(session: "invalid"), "empty-session" => world.Token(session: Guid.Empty.ToString()),
            "no-session" => world.Token(noSession: true), "issuer" => world.Token(issuer: "https://other.invalid"),
            "unsigned" => "eyJhbGciOiJub25lIn0.eyJzdWIiOiJmb28ifQ.", "signature" => world.Token(key: otherKey),
            "duplicate-subject" => world.Token(duplicateSubject: true), "no-expiry" => world.Token(noExpiry: true),
            "algorithm" => world.Token(algorithm: SecurityAlgorithms.RsaSha512), _ => throw new InvalidOperationException()
        };
        using var request = Request(HttpMethod.Get, "/api/v1/users/me", token);
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.False(response.Headers.Contains("X-Upstream"));
        Assert.Equal("AUTHENTICATION_REQUIRED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task JWKS_refresh_accepts_new_key_and_retains_old_key()
    {
        await using var gateway = await world.StartGatewayAsync();
        var old = world.Token();
        using var before = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", old));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var newToken = world.Token(key: world.RotateKey());
        HttpStatusCode status = 0;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            using var response = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", newToken));
            status = response.StatusCode; if (status == HttpStatusCode.OK) break;
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.OK, status);
        using var after = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", old));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Body_query_bearer_idempotency_and_correlation_survive_but_spoofed_headers_do_not()
    {
        await using var gateway = await world.StartGatewayAsync();
        var token = world.Token();
        using var request = Request(HttpMethod.Put, "/api/v1/users/me?name=one%20two&tag=a&tag=b", token);
        request.Content = new StringContent("{\"name\":\"Synthetic\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-ID", "synthetic-flow-1");
        foreach (var header in new[] { "X-User-Authorization", "X-User-Id", "X-Actor-Id", "X-Identity-Id", "X-School-Id", "X-Membership-Id", "X-Permission-Name", "X-Http-Method-Override", "X-Http-Method", "X-Method-Override", "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix" }) request.Headers.Add(header, "spoof");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PUT", echo.GetProperty("method").GetString());
        Assert.Equal("?name=one%20two&tag=a&tag=b", echo.GetProperty("query").GetString());
        Assert.Equal("{\"name\":\"Synthetic\"}", Encoding.UTF8.GetString(Convert.FromBase64String(echo.GetProperty("body").GetString()!)));
        var headers = echo.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Bearer " + token, headers["Authorization"]);
        Assert.Equal(request.Headers.GetValues("Idempotency-Key").Single(), headers["Idempotency-Key"]);
        Assert.Equal("synthetic-flow-1", headers["X-Correlation-ID"]);
        Assert.Equal("synthetic-flow-1", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.DoesNotContain(headers, h => h.Value == "spoof");
    }

    [Fact]
    public async Task Owner_ProblemDetails_and_cookies_are_preserved()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var problem = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me?simulate=problem", world.Token()));
        Assert.Equal(HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("{\"status\":409,\"code\":\"OWNER_CONFLICT\",\"detail\":\"Unchanged owner response\"}", await problem.Content.ReadAsStringAsync());
        using var cookies = await gateway.Client.PostAsync("/api/v1/auth/oidc/requests?simulate=cookies", null);
        Assert.Equal("oidc=opaque; path=/; secure; httponly; samesite=lax", cookies.Headers.GetValues("Set-Cookie").Single());
        Assert.Equal("https://localhost:7000/api/v1/users/me", cookies.Headers.Location?.ToString());
        using var empty = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me?simulate=empty", world.Token()));
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        Assert.Empty(await empty.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("127.0.0.1", "198.51.100.20")]
    [InlineData("192.0.2.10", "127.0.0.1")]
    public async Task Forwarded_client_IP_is_accepted_only_from_an_explicitly_trusted_proxy(string proxy, string expectedClient)
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:TrustedProxies:0"] = proxy });
        using var request = Request(HttpMethod.Get, "/api/v1/users/me", world.Token());
        request.Headers.Add("X-Forwarded-For", "198.51.100.20");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "localhost");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<JsonElement>();
        var headers = echo.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedClient, headers["X-Forwarded-For"]);
    }

    [Theory]
    [InlineData("http://localhost:4200", true)]
    [InlineData("https://untrusted.invalid", false)]
    public async Task CORS_preflight_and_responses_have_only_gateway_policy(string origin, bool allowed)
    {
        await using var gateway = await world.StartGatewayAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/users/me");
        preflight.Headers.Add("Origin", origin); preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");
        using var preflightResponse = await gateway.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal(allowed, preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        using var request = Request(HttpMethod.Get, "/api/v1/users/me?simulate=cors", world.Token()); request.Headers.Add("Origin", origin);
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) { Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single()); Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single()); }
    }

    [Fact]
    public async Task Auth_limit_is_per_IP_and_cannot_be_bypassed_with_forwarded_headers()
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:AuthenticationRequestsPerMinute"] = "1" });
        using var first = await gateway.Client.PostAsync("/api/v1/auth/login", null); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var request = Request(HttpMethod.Post, "/api/v1/auth/refresh"); request.Headers.Add("X-Forwarded-For", "1.2.3.4");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); Assert.NotNull(response.Headers.RetryAfter);
        Assert.False(response.Headers.Contains("X-Upstream"));
    }

    [Fact]
    public async Task User_limit_uses_validated_identity_across_routes_but_isolates_users()
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:UserRequestsPerMinute"] = "1" });
        using var first = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", world.Token())); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var limited = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me/enrollments", world.Token("vairas-school-management"))); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var second = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", world.Token(identity: Guid.NewGuid()))); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Global_IP_limit_also_covers_failed_authentication_and_health_stays_available()
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:IpRequestsPerMinute"] = "1" });
        using var first = await gateway.Client.GetAsync("/api/v1/users/me"); Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        using var limited = await gateway.Client.GetAsync("/api/v1/users/me"); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var live = await gateway.Client.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await gateway.Client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Unavailable_owner_returns_502_without_disabling_other_routes()
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:Services:user-management:Address"] = "http://127.0.0.1:1/" });
        using var response = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", world.Token()));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("UPSTREAM_FAILURE", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var other = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me/enrollments", world.Token("vairas-school-management"))); Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var ready = await gateway.Client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Deadline_returns_504_and_cancels_upstream()
    {
        await using var gateway = await world.StartGatewayAsync(new() { ["Gateway:ApiTimeoutSeconds"] = "1" });
        using var response = await gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me?simulate=slow", world.Token()));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("UPSTREAM_TIMEOUT", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        for (var i = 0; i < 20 && !world.Cancelled.Contains("slow"); i++) await Task.Delay(50);
        Assert.Contains("slow", world.Cancelled);
    }

    [Fact]
    public async Task Client_abort_cancels_upstream_without_retry()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me?simulate=cancel", world.Token()), cts.Token));
        for (var i = 0; i < 20 && !world.Cancelled.Contains("cancel"); i++) await Task.Delay(50);
        Assert.Contains("cancel", world.Cancelled);
    }

    [Fact]
    public async Task Multipart_and_download_preserve_bytes_media_type_and_filename()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var form = new MultipartFormDataContent("synthetic-boundary");
        var data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        form.Add(new ByteArrayContent(data), "file", "synthetic.pdf");
        var serialized = await form.ReadAsByteArrayAsync();
        using var upload = Request(HttpMethod.Post, $"/api/v1/schools/{Guid.NewGuid()}/document-files", world.Token("vairas-document-management")); upload.Content = form;
        using var result = await gateway.Client.SendAsync(upload); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(serialized.Length, body.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(serialized)), body.GetProperty("sha256").GetString());
        Assert.Equal(form.Headers.ContentType?.ToString(), body.GetProperty("contentType").GetString());
        using var download = await gateway.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/document-downloads/{Guid.NewGuid()}?schoolId={Guid.NewGuid()}", world.Token("vairas-document-management")));
        Assert.Equal(GatewayWorld.Download, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("synthetic.pdf", download.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Upload_above_21_MiB_is_rejected()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var request = Request(HttpMethod.Post, $"/api/v1/schools/{Guid.NewGuid()}/document-files", world.Token("vairas-document-management"));
        request.Content = new ByteArrayContent(new byte[21 * 1024 * 1024 + 1]);
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Chunked_upload_cannot_bypass_the_request_body_limit()
    {
        await using var gateway = await world.StartGatewayAsync();
        using var request = Request(HttpMethod.Post, $"/api/v1/schools/{Guid.NewGuid()}/document-files", world.Token("vairas-document-management"));
        request.Content = new ChunkedContent(21 * 1024 * 1024 + 1);
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class ChunkedContent(int size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(new byte[size]);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Diagnostics_do_not_log_credentials_payload_or_query_secrets()
    {
        var logs = new CaptureLogs();
        await using var gateway = await world.StartGatewayAsync(logs: logs);
        using var request = Request(HttpMethod.Post, "/api/v1/auth/login?code=secret-query-marker");
        request.Headers.Add("Cookie", "oidc=secret-cookie-marker");
        request.Headers.Authorization = new("Bearer", world.Token());
        request.Content = new StringContent("{\"password\":\"secret-body-marker\"}");
        using var response = await gateway.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var output = string.Join('\n', logs.Messages);
        Assert.Contains("authentication", output);
        foreach (var secret in new[] { "secret-query-marker", "secret-cookie-marker", "secret-body-marker", request.Headers.Authorization.Parameter! }) Assert.DoesNotContain(secret, output);
    }
}
