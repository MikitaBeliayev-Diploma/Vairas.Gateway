using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vairas.ConfirmedActions;
using Vairas.Delegation;
using Xunit;

namespace Vairas.Gateway.Tests;

/// <summary>
/// The Gateway enforces the shared ReadPreviewV1 / ConfirmedLessonActionsV1 policy (byte-identical copies checked by
/// McpServer/scripts/sync-policy.py) before proxying, without buffering bodies (checkBody: false).
/// </summary>
public sealed class DelegationPolicyTests
{
    private const string Chain = "{\"sub\":\"mcp-server\",\"act\":{\"sub\":\"chat-backend\"}}";
    private const string Lesson = "11111111-1111-4111-8111-111111111111";
    private readonly Guid school = Guid.NewGuid();

    private static string Exp(int minutes = 1) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds().ToString();

    private DefaultHttpContext Delegated(string owner, string scope, string path, string method = "GET", string? query = null, params (string Type, string Value)[] replace)
    {
        var claims = new Dictionary<string, string>
        {
            ["sub"] = Guid.NewGuid().ToString(), ["sid"] = Guid.NewGuid().ToString(), ["school_id"] = school.ToString(), ["scope"] = scope,
            ["vairas_delegation"] = ReadPreviewDelegation.Profile, ["aud"] = "vairas-" + owner, ["act"] = Chain, ["exp"] = Exp()
        };
        foreach (var (type, value) in replace) claims[type] = value;
        var http = new DefaultHttpContext();
        http.Request.Method = method; http.Request.Path = path;
        if (query is not null) http.Request.QueryString = new(query);
        http.User = new(new ClaimsIdentity(claims.Where(c => c.Value is not null).Select(c => new Claim(c.Key, c.Value)), "synthetic"));
        return http;
    }

    [Theory]
    [InlineData("user-management", "mcp.context.read", "/api/v1/users/me", null)]
    [InlineData("school-management", "mcp.get_my_enrollments", "/api/v1/users/me/enrollments", null)]
    [InlineData("scheduling", "mcp.get_my_lessons", "/api/v1/users/me/lessons", "school")]
    [InlineData("learning-tracking", "mcp.get_student_training_progress", "/api/v1/student-trainings/" + Lesson + "/progress", "school")]
    public async Task Exact_read_routes_with_matching_scope_audience_and_school_are_admitted(string owner, string scope, string path, string? query)
    {
        var http = Delegated(owner, scope, path, query: query is null ? null : "?schoolId=" + school);
        Assert.True(await ReadPreviewDelegation.AllowedAsync(http, owner, false));
        http.Request.Path = path + "/unpublished";
        Assert.False(await ReadPreviewDelegation.AllowedAsync(http, owner, false));
    }

    [Fact]
    public async Task Availability_is_bound_to_the_delegated_school_in_the_path()
    {
        Assert.True(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.find_available_lessons", $"/api/v1/schools/{school}/availability"), "scheduling", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.find_available_lessons", $"/api/v1/schools/{Guid.NewGuid()}/availability"), "scheduling", false));
    }

    [Fact]
    public async Task Query_scoped_reads_require_exactly_one_matching_school()
    {
        const string path = "/api/v1/users/me/lessons";
        Assert.False(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.get_my_lessons", path), "scheduling", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.get_my_lessons", path, query: $"?schoolId={Guid.NewGuid()}"), "scheduling", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.get_my_lessons", path, query: $"?schoolId={school}&schoolId={school}"), "scheduling", false));
        Assert.True(await ReadPreviewDelegation.AllowedAsync(Delegated("scheduling", "mcp.preview_lesson_rescheduling", $"/api/v1/practical-lessons/{Lesson}/rescheduling-previews", "POST", $"?schoolId={school}"), "scheduling", false));
    }

    [Fact]
    public async Task Gateway_streams_booking_preview_bodies_and_leaves_body_binding_to_the_owner()
    {
        var http = Delegated("scheduling", "mcp.preview_lesson_booking", "/api/v1/practical-lessons/booking-previews", "POST");
        Assert.True(await ReadPreviewDelegation.AllowedAsync(http, "scheduling", false));
    }

    [Theory]
    [InlineData("match", true)][InlineData("other", false)][InlineData("duplicate", false)][InlineData("array", false)][InlineData("oversized", false)]
    public async Task Owner_side_body_check_binds_booking_preview_to_the_delegated_school(string variant, bool expected)
    {
        var http = Delegated("scheduling", "mcp.preview_lesson_booking", "/api/v1/practical-lessons/booking-previews", "POST");
        var body = variant switch
        {
            "match" => $"{{\"schoolId\":\"{school}\"}}", "other" => $"{{\"schoolId\":\"{Guid.NewGuid()}\"}}",
            "duplicate" => $"{{\"schoolId\":\"{school}\",\"schoolId\":\"{school}\"}}", "array" => "[]", _ => "{}"
        };
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.ContentLength = variant == "oversized" ? 65537 : body.Length;
        Assert.Equal(expected, await ReadPreviewDelegation.AllowedAsync(http, "scheduling", true));
        if (variant != "oversized") Assert.Equal(0, http.Request.Body.Position);
    }

    [Theory]
    [InlineData("scheduling", "/api/v1/practical-lessons")]
    [InlineData("scheduling", "/api/v1/practical-lessons/" + Lesson + "/rescheduling")]
    [InlineData("user-management", "/api/v1/users")]
    [InlineData("vehicle-management", "/api/v1/schools/" + Lesson + "/vehicles")]
    public async Task Business_writes_and_unpublished_owners_are_denied_with_every_read_preview_scope(string owner, string path)
    {
        var http = Delegated(owner, string.Join(' ', ReadPreviewDelegation.Scopes), path, "POST");
        Assert.False(await ReadPreviewDelegation.AllowedAsync(http, owner, false));
    }

    [Theory]
    [InlineData("audience")][InlineData("scope")][InlineData("unknown-scope")][InlineData("repeated-scope")][InlineData("empty-scope")]
    [InlineData("expired")][InlineData("profile")][InlineData("direct-actor")][InlineData("wrong-actor")][InlineData("identity")]
    public async Task Invalid_delegations_are_denied(string variant)
    {
        (string, string) change = variant switch
        {
            "audience" => ("aud", "vairas-school-management"), "scope" => ("scope", "mcp.get_my_enrollments"),
            "unknown-scope" => ("scope", "mcp.get_my_lessons mcp.create_lesson_booking"), "repeated-scope" => ("scope", "mcp.get_my_lessons mcp.get_my_lessons"),
            "empty-scope" => ("scope", " "), "expired" => ("exp", Exp(-1)), "profile" => ("vairas_delegation", "ReadPreviewV2"),
            "direct-actor" => ("act", "{\"sub\":\"chat-backend\"}"), "wrong-actor" => ("act", "{\"sub\":\"mcp-server\",\"act\":{\"sub\":\"other\"}}"),
            _ => ("sub", Guid.Empty.ToString())
        };
        var http = Delegated("scheduling", "mcp.get_my_lessons", "/api/v1/users/me/lessons", query: "?schoolId=" + school, replace: change);
        Assert.False(await ReadPreviewDelegation.AllowedAsync(http, "scheduling", false));
    }

    [Fact]
    public async Task Duplicate_binding_claims_and_unauthenticated_marked_principals_are_denied()
    {
        var http = Delegated("scheduling", "mcp.get_my_lessons", "/api/v1/users/me/lessons", query: "?schoolId=" + school);
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("school_id", Guid.NewGuid().ToString()));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(http, "scheduling", false));
        var anonymous = new DefaultHttpContext { User = new(new ClaimsIdentity([new Claim("school_id", school.ToString())])) };
        Assert.False(await ReadPreviewDelegation.AllowedAsync(anonymous, "scheduling", false));
    }

    [Fact]
    public async Task Ordinary_user_tokens_are_not_subject_to_the_delegation_policy()
    {
        var http = new DefaultHttpContext { User = new(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "user")) };
        http.Request.Method = "POST"; http.Request.Path = "/api/v1/practical-lessons";
        Assert.False(ReadPreviewDelegation.Marked(http.User));
        Assert.True(await ReadPreviewDelegation.AllowedAsync(http, "scheduling", false));
        var called = false;
        await ReadPreviewDelegation.EnforceAsync(http, _ => { called = true; return Task.CompletedTask; }, "scheduling", false);
        Assert.True(called);
    }

    [Fact]
    public async Task Denial_returns_problem_details_without_calling_the_proxy()
    {
        var http = Delegated("scheduling", "mcp.get_my_lessons", "/api/v1/practical-lessons", "POST");
        http.Response.Body = new MemoryStream(); http.TraceIdentifier = "trace-1";
        var called = false;
        await ReadPreviewDelegation.EnforceAsync(http, _ => { called = true; return Task.CompletedTask; }, "scheduling", false);
        Assert.False(called);
        Assert.Equal(403, http.Response.StatusCode);
        Assert.StartsWith("application/problem+json", http.Response.ContentType);
        http.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.Equal("DELEGATION_DENIED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("trace-1", problem.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void Route_and_resource_scope_tables_publish_only_read_preview_operations()
    {
        Assert.Equal(7, ReadPreviewDelegation.Scopes.Length);
        Assert.Contains(ReadPreviewDelegation.ContextScope, ReadPreviewDelegation.Scopes);
        Assert.Null(ReadPreviewDelegation.RouteScope("scheduling", "DELETE", "/api/v1/users/me/lessons"));
        Assert.Null(ReadPreviewDelegation.RouteScope("chat", "GET", "/api/v1/users/me"));
        Assert.Equal("mcp.get_my_lessons", ReadPreviewDelegation.RouteScope("scheduling", "GET", "/API/V1/USERS/ME/LESSONS/"));
        Assert.Equal(new[] { ReadPreviewDelegation.ContextScope }, ReadPreviewDelegation.ResourceScopes("user-management"));
        Assert.Equal(new[] { "mcp.get_my_enrollments" }, ReadPreviewDelegation.ResourceScopes("school-management"));
        Assert.Equal(4, ReadPreviewDelegation.ResourceScopes("scheduling").Length);
        Assert.Equal(new[] { "mcp.get_student_training_progress" }, ReadPreviewDelegation.ResourceScopes("learning-tracking"));
        Assert.Empty(ReadPreviewDelegation.ResourceScopes("document-management"));
    }

    // ConfirmedLessonActionsV1 execution tokens: only the exact confirmed command endpoint on Scheduling.
    private static readonly JsonElement Arguments = JsonSerializer.SerializeToElement(new { lessonId = Lesson, startsAt = "2026-11-26T09:00:00Z", minutes = 45 });
    private static readonly JsonElement Sources = JsonSerializer.SerializeToElement(new { schoolRevision = 3, lessonVersion = 1 });

    private LessonActionBinding Binding(string kind = "Booking") => new(school, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        kind, new string('a', 64), LessonActions.Digest(kind, Arguments, Sources));

    private static ClaimsPrincipal Execution(LessonActionBinding b, string actor = Chain, params (string Type, string Value)[] replace)
    {
        var claims = new Dictionary<string, string>
        {
            ["vairas_delegation"] = LessonActions.Profile, [LessonActions.Claim] = JsonSerializer.Serialize(b, LessonActions.Json), ["act"] = actor,
            ["sub"] = b.IdentityId.ToString("D"), ["sid"] = b.SessionId.ToString("D"), ["school_id"] = b.SchoolId.ToString("D"),
            ["scope"] = LessonActions.Scope(b.Kind), ["exp"] = Exp(), ["aud"] = "vairas-scheduling"
        };
        foreach (var (type, value) in replace) claims[type] = value;
        return new(new ClaimsIdentity(claims.Select(c => new Claim(c.Key, c.Value)), "synthetic"));
    }

    private static DefaultHttpContext ExecutionRequest(ClaimsPrincipal user, string path, LessonAction? body = null, string? key = null)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.Method = "POST"; http.Request.Path = path;
        if (body is not null) http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body, LessonActions.Json));
        if (key is not null) http.Request.Headers["Idempotency-Key"] = key;
        return http;
    }

    [Theory]
    [InlineData("Booking", "/api/v1/practical-lessons/confirmed-bookings")]
    [InlineData("Rescheduling", "/api/v1/practical-lessons/confirmed-reschedulings")]
    public async Task Confirmed_execution_reaches_only_its_exact_scheduling_command(string kind, string path)
    {
        var b = Binding(kind);
        Assert.True(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path), "scheduling", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), "/api/v1/practical-lessons"), "scheduling", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path), "school-management", false));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b, replace: ("aud", "vairas-gateway")), path), "scheduling", false));
    }

    [Fact]
    public async Task Owner_side_execution_body_must_equal_the_signed_binding_and_operation()
    {
        var b = Binding(); const string path = "/api/v1/practical-lessons/confirmed-bookings";
        var exact = new LessonAction(b, Arguments, Sources);
        Assert.True(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path, exact, b.OperationId.ToString("D")), "scheduling", true));
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path, exact, Guid.NewGuid().ToString("D")), "scheduling", true));
        var swapped = exact with { Binding = b with { PreviewId = Guid.NewGuid() } };
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path, swapped, b.OperationId.ToString("D")), "scheduling", true));
        var tampered = exact with { Arguments = JsonSerializer.SerializeToElement(new { lessonId = Lesson, startsAt = "2026-11-27T09:00:00Z", minutes = 45 }) };
        Assert.False(await ReadPreviewDelegation.AllowedAsync(ExecutionRequest(Execution(b), path, tampered, b.OperationId.ToString("D")), "scheduling", true));
    }

    [Fact]
    public void Lesson_action_names_scopes_and_schemas_are_stable()
    {
        Assert.Equal("create_lesson_booking", LessonActions.Tool("Booking"));
        Assert.Equal("mcp.reschedule_lesson_booking", LessonActions.Scope("Rescheduling"));
        Assert.Throws<InvalidOperationException>(() => LessonActions.Tool("Cancellation"));
        Assert.Equal(3, LessonActions.InputSchema.GetProperty("required").GetArrayLength());
        Assert.Equal(new string?[] { "Committed", "Rejected", "Unknown" }, LessonActions.OutputSchema.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.True(LessonActions.IsExecution(Execution(Binding())));
        Assert.False(LessonActions.IsExecution(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Command_hash_is_canonical_and_rejects_ambiguous_json()
    {
        using var a = JsonDocument.Parse("{\"b\":[1,true,null,\"x\"],\"a\":{\"d\":false,\"c\":-2}}");
        using var b = JsonDocument.Parse("{\"a\":{\"c\":-2,\"d\":false},\"b\":[1,true,null,\"x\"]}");
        Assert.Equal(LessonActions.Hash(a.RootElement), LessonActions.Hash(b.RootElement));
        Assert.Matches("^[0-9a-f]{64}$", LessonActions.Hash(a.RootElement));
        using var order = JsonDocument.Parse("{\"b\":[\"x\",1],\"a\":{}}");
        Assert.NotEqual(LessonActions.Hash(order.RootElement), LessonActions.Hash(JsonDocument.Parse("{\"b\":[1,\"x\"],\"a\":{}}").RootElement));
        foreach (var json in new[] { "{\"a\":1,\"a\":2}", "{\"n\":1.5}", "{\"n\":9007199254740992}" })
        {
            using var bad = JsonDocument.Parse(json);
            Assert.Throws<InvalidOperationException>(() => LessonActions.Hash(bad.RootElement));
        }
    }

    [Theory]
    [InlineData("valid", true)][InlineData("empty-id", false)][InlineData("upper-digest", false)][InlineData("short-digest", false)]
    [InlineData("command", false)][InlineData("arguments", false)][InlineData("kind", false)]
    public void Validate_accepts_only_a_self_consistent_binding(string variant, bool valid)
    {
        var b = Binding();
        var action = variant switch
        {
            "empty-id" => new LessonAction(b with { SessionId = Guid.Empty }, Arguments, Sources),
            "upper-digest" => new LessonAction(b with { ActionDigest = new string('A', 64) }, Arguments, Sources),
            "short-digest" => new LessonAction(b with { ActionDigest = "abc" }, Arguments, Sources),
            "command" => new LessonAction(b with { CommandDigest = new string('b', 64) }, Arguments, Sources),
            "arguments" => new LessonAction(b with { CommandDigest = LessonActions.Digest("Booking", JsonSerializer.SerializeToElement(1), Sources) }, JsonSerializer.SerializeToElement(1), Sources),
            "kind" => new LessonAction(b with { Kind = "Cancellation", CommandDigest = LessonActions.Digest("Cancellation", Arguments, Sources) }, Arguments, Sources),
            _ => new LessonAction(b, Arguments, Sources)
        };
        if (valid) LessonActions.Validate(action);
        else Assert.Throws<InvalidOperationException>(() => LessonActions.Validate(action));
    }

    [Fact]
    public void Parse_binds_execution_to_the_exact_actor_chain_depth_and_identity()
    {
        var b = Binding();
        Assert.Equal(b, LessonActions.Parse(Execution(b), 2));
        Assert.Equal(b, LessonActions.Parse(Execution(b, "{\"sub\":\"chat-backend\"}"), 1));
        Assert.Throws<InvalidOperationException>(() => LessonActions.Parse(Execution(b), 1));
        Assert.Throws<InvalidOperationException>(() => LessonActions.Parse(Execution(b), 3));
        foreach (var change in new (string, string)[] { ("vairas_delegation", "ReadPreviewV1"), ("sub", Guid.NewGuid().ToString()), ("sid", Guid.NewGuid().ToString()),
                     ("school_id", Guid.NewGuid().ToString()), ("scope", "mcp.reschedule_lesson_booking"), ("exp", Exp(-1)), ("exp", "soon") })
            Assert.Throws<InvalidOperationException>(() => LessonActions.Parse(Execution(b, replace: change), 2));
        var weak = b with { ActionDigest = "x" };
        Assert.Throws<InvalidOperationException>(() => LessonActions.Parse(Execution(weak), 2));
    }
}
