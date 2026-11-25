using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace Vairas.Gateway.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData(ForwarderError.RequestTimedOut, false, 504)]
    [InlineData(ForwarderError.RequestCanceled, true, 504)]
    [InlineData(ForwarderError.NoAvailableDestinations, false, 503)]
    [InlineData(ForwarderError.Request, false, 502)]
    public void Transport_failures_have_stable_status_codes(ForwarderError error, bool deadline, int expected)
    {
        var result = RequestBoundary.Classify(new TestErrorFeature(error), deadline);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(400, "INVALID_REQUEST")][InlineData(413, "REQUEST_TOO_LARGE")]
    public void Client_body_errors_are_preserved_through_nested_proxy_exceptions(int status, string code)
    {
        var error = new TestErrorFeature(ForwarderError.RequestBodyClient, new IOException("Copy failed.", new BadHttpRequestException("Client body rejected.", status)));
        Assert.Equal((status, code), RequestBoundary.Classify(error, false));
    }

    [Fact]
    public async Task Nonlocal_cleartext_is_denied_and_does_not_reach_the_proxy()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = Context(services); http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        var reached = false;
        var boundary = new RequestBoundary(_ => { reached = true; return Task.CompletedTask; }, NullLogger<RequestBoundary>.Instance);
        await boundary.InvokeAsync(http);
        Assert.False(reached); Assert.Equal(403, http.Response.StatusCode);
    }

    [Theory]
    [InlineData("bad correlation value")][InlineData("")]
    public async Task Invalid_correlation_is_replaced_and_unhandled_errors_have_safe_ProblemDetails(string value)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = Context(services); http.Request.Headers["X-Correlation-ID"] = value;
        var boundary = new RequestBoundary(_ => throw new InvalidOperationException("secret exception detail"), NullLogger<RequestBoundary>.Instance);
        await boundary.InvokeAsync(http);
        Assert.Equal(500, http.Response.StatusCode);
        Assert.True(Guid.TryParse(http.Items["CorrelationId"]?.ToString(), out _));
        http.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.Equal("GATEWAY_FAILURE", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("secret exception detail", json.RootElement.GetRawText());
    }

    [Fact]
    public async Task Cancelled_clients_are_not_given_a_new_error_response()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = Context(services); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); http.RequestAborted = cancelled.Token;
        var boundary = new RequestBoundary(_ => throw new OperationCanceledException(), NullLogger<RequestBoundary>.Instance);
        await boundary.InvokeAsync(http);
        Assert.Equal(0, http.Response.Body.Length);
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Connection.RemoteIpAddress = IPAddress.Loopback; http.Request.Path = "/api/v1/users/me"; http.Response.Body = new MemoryStream(); return http;
    }

    private sealed record TestErrorFeature(ForwarderError Error, Exception? Exception = null) : IForwarderErrorFeature;
}
