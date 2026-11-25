extern alias GatewayApi;
extern alias SchoolTests;
extern alias DocumentsApi;
extern alias UserApi;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;
using Vairas.Authentication.Business;
using Vairas.Authentication.Domain;
using Vairas.Authentication.Persistance;
using Vairas.DocumentManagement.Business;
using DocumentsApi::Vairas.DocumentManagement.Integrations;
using Vairas.DocumentManagement.Persistance;
using Vairas.SchoolManagement.Domain;
using EnvironmentFixture = SchoolTests::Vairas.SchoolManagement.Tests.IntegrationEnvironment;
using UserJwt = UserApi::Vairas.UserManagement.Configuration.Options.JwtOptions;

namespace Vairas.Gateway.ServiceTests;

// These tests use real owner applications/DBs, with TCP adapters in front of their
// TestServer hosts. Blob, scanner and PDF rendering are explicit fixtures.
[TestFixture, NonParallelizable, Category("Services")]
public sealed class ServiceSmokeTests
{
    [Test]
    public async Task Gateway_login_profile_and_authenticated_contract_download_retain_owner_authorization()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var postgres = new PostgreSqlBuilder("postgres:17").WithPassword(secret).Build();
        await postgres.StartAsync();
        var previous = System.Environment.GetEnvironmentVariable("VAIRAS_TEST_ADMIN_CONNECTION");
        System.Environment.SetEnvironmentVariable("VAIRAS_TEST_ADMIN_CONNECTION", postgres.GetConnectionString());
        await using var owners = new EnvironmentFixture();
        var bridges = new List<WebApplication>();
        var suffix = Guid.NewGuid().ToString("N");
        var ownerRole = "gw_dm_owner_" + suffix; var runtimeRole = "gw_dm_runtime_" + suffix; var deliveryRole = "gw_dm_delivery_" + suffix; var database = "gw_documents_" + suffix;
        try
        {
            owners.ConfigureAuth = c =>
            {
                c["Authentication:WorkloadClients:document-management:SecretHash"] = c["Authentication:WorkloadClients:school-management:SecretHash"];
                var scopes = new[] { "identity.validate", "profiles.status.read", "profiles.binding.read", "profiles.documents.read", "school.documents.read" };
                for (var i = 0; i < scopes.Length; i++) c[$"Authentication:WorkloadClients:document-management:Scopes:{i}"] = scopes[i];
            };
            owners.ConfigureUsers = services => services.Configure<UserJwt>(o => o.WorkloadClients["document-management"] = ["profiles.status.read", "profiles.binding.read", "profiles.documents.read"]);
            owners.ConfigureSchool = c => c["Authentication:WorkloadClients:document-management:0"] = "school.documents.read";
            await owners.StartAsync();
            var backend = EnvironmentFixture.Backend;
            var admin = postgres.GetConnectionString();
            foreach (var role in new[] { ownerRole, runtimeRole, deliveryRole })
                await EnvironmentFixture.Sql(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await EnvironmentFixture.Sql(admin, $"CREATE DATABASE {database} OWNER {ownerRole}");
            string Connection(string role) => new NpgsqlConnectionStringBuilder(admin) { Database = database, Username = role, Password = secret }.ConnectionString;
            await using (var db = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(Connection(ownerRole)).Options)) await db.Database.MigrateAsync();
            var grants = Vairas.DocumentManagement.Persistance.Migrations.Sql.Read("runtime-grants.sql");
            grants = grants.Replace("v_document_management_migration", ownerRole).Replace("v_document_management_runtime", runtimeRole).Replace("v_document_management_delivery", deliveryRole);
            await EnvironmentFixture.Sql(Connection(ownerRole), grants);
            var documentsConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DocumentManagement"] = Connection(runtimeRole), ["Documents:WorkersEnabled"] = "false", ["Dependencies:ClientSecret"] = owners.Secret,
                ["Dependencies:Scope"] = "identity.validate profiles.status.read profiles.binding.read profiles.documents.read school.documents.read"
            };
            await using var documentsFactory = new DocumentsFactory(backend, owners, documentsConfig);
            using var documents = documentsFactory.CreateClient(new() { BaseAddress = new("https://localhost:7700") });
            var destinations = new Dictionary<string, string>();
            foreach (var (name, ownerClient) in new[] { ("authentication", owners.Auth), ("user-management", owners.Users), ("school-management", owners.Client), ("document-management", documents) })
            {
                var bridge = await BridgeAsync(ownerClient); bridges.Add(bridge); destinations[name] = Address(bridge) + "/";
            }
            var gatewayDirectory = Path.Combine(backend, "Vairas.Gateway", "Vairas.Gateway");
            await using var gateway = GatewayApi::Vairas.Gateway.GatewayApplication.Build(["--environment", "Development"], builder =>
            {
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddJsonFile(Path.Combine(gatewayDirectory, "appsettings.json")).AddJsonFile(Path.Combine(gatewayDirectory, "routes.json"));
                builder.Configuration.AddInMemoryCollection(destinations.ToDictionary(p => $"Gateway:Services:{p.Key}:Address", p => (string?)p.Value));
                builder.Configuration["AllowedHosts"] = "127.0.0.1";
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                foreach (var service in GatewayApi::Vairas.Gateway.Configuration.ProxyConfiguration.ServiceNames)
                    builder.Services.Configure<JwtBearerOptions>(service, options => options.ConfigurationManager = Metadata(owners.Auth));
            });
            await gateway.StartAsync();
            using var client = new HttpClient { BaseAddress = new(Address(gateway)) };
            documentsFactory.Services.GetRequiredService<DocumentOptions>().PublicOrigin = Address(gateway);

            // Provision a verified synthetic identity; no real email is sent.
            var email = suffix + "@example.test"; var password = "Synthetic-" + secret;
            await Json(client, HttpMethod.Post, "/api/v1/auth/registrations", null, new { email, password }, HttpStatusCode.Accepted);
            using (var scope = owners.AuthFactory.Services.CreateScope())
            {
                await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(owners.AuthOwner).Options);
                var delivery = await (from d in db.Set<ChallengeDelivery>() join c in db.Set<AccountChallenge>() on d.ChallengeId equals c.ChallengeId where c.LoginEmail == email select d).SingleAsync();
                var mail = JsonSerializer.Deserialize<ChallengeMail>(scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect("mail", delivery.ProtectedPayload!))!;
                await scope.ServiceProvider.GetRequiredService<IAuthenticationService>().CompleteAsync(new(mail.Token, null), new(Guid.NewGuid()), default);
            }
            var tokens = await Json(client, HttpMethod.Post, "/api/v1/auth/login", null, new { email, password });
            var userToken = tokens.GetProperty("accessToken").GetString()!;
            await Json(client, HttpMethod.Post, "/api/v1/users/me", userToken, new { firstName = "Synthetic", lastName = "Gateway", emailAddress = (string?)null, phoneNumber = (string?)null }, HttpStatusCode.Created);
            var profile = await Json(client, HttpMethod.Get, "/api/v1/users/me", userToken);
            Assert.That(profile.GetProperty("firstName").GetString(), Is.EqualTo("Synthetic"));

            var school = await owners.SeedSchool(); var actor = owners.Administrator.Tokens.AccessToken;
            var studentToken = owners.Outsider.Tokens.AccessToken;
            await Json(client, HttpMethod.Post, "/api/v1/users/me/student-profile", studentToken, new { }, HttpStatusCode.Created);
            var studentProfile = await Json(client, HttpMethod.Get, "/api/v1/users/me", studentToken);
            // Pre-existing enrollment is synthetic setup. Enrollment activation requires
            // Scheduling and is covered by its separate integration suite, not this smoke.
            var enrollmentId = Guid.NewGuid(); var setupOperation = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using (var db = owners.OwnerDb())
            {
                db.Add(new SchoolOperation { SchoolId = school, OperationId = setupOperation, CommandName = "IsolatedGatewayFixture", TargetKind = "StudentEnrollment", TargetId = enrollmentId,
                    ActorUserId = owners.Administrator.UserId, Channel = OperationChannel.Internal, Status = OperationStatus.Committed, RequestFingerprint = new string('a', 64),
                    RequestedAt = now, ResolvedAt = now, AuthorityBasis = "Synthetic pre-existing enrollment", OutcomeCode = "TEST_ONLY" });
                db.Add(new StudentEnrollment { SchoolId = school, EnrollmentId = enrollmentId, StudentId = studentProfile.GetProperty("studentId").GetGuid(), CategoryCode = "B",
                    Status = EnrollmentStatus.Active, StartedAt = now, StatusChangedAt = now, Revision = 1, LastOperationId = setupOperation });
                await db.SaveChangesAsync();
            }
            var template = await Json(client, HttpMethod.Post, $"/api/v1/schools/{school}/document-templates", actor, new CreateTemplateRequest("TrainingContract", "Synthetic Gateway contract"), HttpStatusCode.Created);
            var templateId = template.GetProperty("templateId").GetGuid();
            using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/schools/{school}/document-files?purpose=Template");
            upload.Headers.Authorization = new("Bearer", actor); upload.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(DocxRenderer.DemoTemplate("TrainingContract")), "file", "synthetic.docx"); upload.Content = form;
            using var uploaded = await client.SendAsync(upload); Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.Created), await uploaded.Content.ReadAsStringAsync());
            var file = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
            template = await Json(client, HttpMethod.Post, $"/api/v1/schools/{school}/document-templates/{templateId}/versions", actor, new { fileId = file.GetProperty("fileId").GetGuid(), expectedRevision = template.GetProperty("revision").GetInt64() }, HttpStatusCode.Created);
            var templateVersion = template.GetProperty("versions")[0].GetProperty("templateVersionId").GetGuid();
            await Json(client, HttpMethod.Post, $"/api/v1/schools/{school}/document-templates/{templateId}/versions/{templateVersion}/activate", actor, new RevisionRequest(template.GetProperty("revision").GetInt64(), "Activate synthetic fixture."));
            var job = await Json(client, HttpMethod.Post, $"/api/v1/schools/{school}/contracts", actor, new GenerateContractRequest(enrollmentId, templateId), HttpStatusCode.Accepted);
            var jobId = job.GetProperty("jobId").GetGuid();
            using (var scope = documentsFactory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<DocumentService>().ProcessJobAsync(school, jobId, default);
            job = await Json(client, HttpMethod.Get, $"/api/v1/document-jobs/{jobId}?schoolId={school}", actor);
            Assert.That(job.GetProperty("status").GetString(), Is.EqualTo("Completed"), job.GetRawText());
            var document = job.GetProperty("documentId").GetGuid();
            var grant = await Json(client, HttpMethod.Get, $"/api/v1/documents/{document}/download?schoolId={school}", actor);
            var downloadUrl = grant.GetProperty("downloadUrl").GetString()!;
            Assert.That(downloadUrl, Does.StartWith(Address(gateway) + "/api/v1/document-downloads/"));
            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
            {
                request.Headers.Authorization = new("Bearer", actor);
                using var content = await client.SendAsync(request);
                Assert.That(content.StatusCode, Is.EqualTo(HttpStatusCode.OK), await content.Content.ReadAsStringAsync());
                Assert.That(content.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/pdf"));
                Assert.That(await content.Content.ReadAsByteArrayAsync(), Is.EqualTo(Renderer.Pdf));
            }
            await Json(client, HttpMethod.Get, downloadUrl, studentToken, expected: HttpStatusCode.NotFound);
            await Json(client, HttpMethod.Get, downloadUrl, null, expected: HttpStatusCode.Unauthorized);
            await Json(client, HttpMethod.Post, $"/api/v1/auth/sessions/{owners.Administrator.Tokens.SessionId}/revocation", actor, new { });
            // Gateway still accepts the signed JWT; the real owner rejects its revoked session.
            await Json(client, HttpMethod.Get, downloadUrl, actor, expected: HttpStatusCode.Unauthorized);
            TestContext.Out.WriteLine("Real Gateway TCP -> owner HTTP adapters -> Authentication/User/School/Document pipelines; four PostgreSQL databases with restricted runtime roles. Login, profile, upload, contract generation, Gateway download URL, actor binding and revoked-session rejection passed. Blob/ClamAV/PDF are fixtures; SMTP/broker/cloud were not exercised.");
        }
        finally
        {
            foreach (var bridge in bridges) await bridge.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await EnvironmentFixture.Sql(postgres.GetConnectionString(), $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            foreach (var role in new[] { runtimeRole, deliveryRole, ownerRole }) await EnvironmentFixture.Sql(postgres.GetConnectionString(), $"DROP ROLE IF EXISTS {role}");
            System.Environment.SetEnvironmentVariable("VAIRAS_TEST_ADMIN_CONNECTION", previous);
        }
    }

    private static async Task<JsonElement> Json(HttpClient client, HttpMethod method, string path, string? token, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) { request.Content = JsonContent.Create(body); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); }
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(expected), text);
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static ConfigurationManager<OpenIdConnectConfiguration> Metadata(HttpClient auth) => new("https://localhost:7100/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(auth) { RequireHttps = true });

    private static async Task<WebApplication> BridgeAsync(HttpClient target)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Configuration["AllowedHosts"] = "127.0.0.1"; builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async http =>
        {
            using var request = new HttpRequestMessage(new(http.Request.Method), new Uri(target.BaseAddress!, http.Request.Path + http.Request.QueryString));
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(http.Request.Body);
            foreach (var header in http.Request.Headers)
                if (header.Key is not ("Host" or "Connection" or "Transfer-Encoding" or "Content-Length") && !request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            using var response = await target.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (header.Key is not ("Transfer-Encoding" or "Connection")) http.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        });
        await app.StartAsync(); return app;
    }

    private sealed class DocumentsFactory(string backend, EnvironmentFixture owners, Dictionary<string, string?> config) : WebApplicationFactory<DocumentsApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var root = Path.Combine(backend, "Vairas.DocumentManagement", "Vairas.DocumentManagement");
            builder.UseEnvironment("Testing").UseContentRoot(root);
            builder.ConfigureAppConfiguration((_, configuration) => { configuration.Sources.Clear(); configuration.AddJsonFile(Path.Combine(root, "appsettings.json")).AddInMemoryCollection(config); });
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IPrivateFiles, Files>(); services.AddSingleton<IFileScanner, Scanner>(); services.AddSingleton<IDocumentRenderer, Renderer>();
                services.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(() => new Dependencies(owners));
                foreach (var scheme in new[] { "User", "Workload" }) services.Configure<JwtBearerOptions>(scheme, options => options.ConfigurationManager = Metadata(owners.Auth));
            });
        }
    }
    private sealed class Dependencies(EnvironmentFixture owners) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.RequestUri!.Port switch { 7100 => owners.Auth, 7001 => owners.Users, 7200 => owners.Client, _ => throw new InvalidOperationException("Unexpected owner.") };
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null) { copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct)); foreach (var header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return await target.SendAsync(copy, ct);
        }
    }
    private sealed class Files : IPrivateFiles
    {
        private readonly Dictionary<string, byte[]> files = [];
        public Task PutAsync(string key, byte[] bytes, string mediaType, CancellationToken ct) { files.Add(key, bytes); return Task.CompletedTask; }
        public Task<byte[]> GetAsync(string key, long expectedSize, CancellationToken ct) => Task.FromResult(files[key]);
    }
    private sealed class Scanner : IFileScanner { public Task<string> ScanAsync(byte[] bytes, CancellationToken ct) => Task.FromResult("Clean"); }
    private sealed class Renderer : IDocumentRenderer
    {
        public static readonly byte[] Pdf = "%PDF-1.7 Synthetic Gateway contract transport fixture"u8.ToArray();
        private readonly DocxRenderer renderer = new(new(), new());
        public void ValidateTemplate(byte[] bytes, string kind) => renderer.ValidateTemplate(bytes, kind);
        public Task<RenderOutput> RenderAsync(byte[] bytes, RenderBasis basis, CancellationToken ct) => Task.FromResult(new RenderOutput(renderer.Fill(bytes, basis), Pdf, "gateway-smoke-fixture"));
    }
}
