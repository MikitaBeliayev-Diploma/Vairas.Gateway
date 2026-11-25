using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Vairas.Gateway;

public sealed partial class RequestBoundary(RequestDelegate next, ILogger<RequestBoundary> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var supplied = http.Request.Headers["X-Correlation-ID"];
        var correlation = supplied.Count == 1 && CorrelationPattern().IsMatch(supplied[0]!) ? supplied[0]! : Guid.NewGuid().ToString("D");
        http.Items["CorrelationId"] = correlation;
        http.Request.Headers["X-Correlation-ID"] = correlation;
        http.Response.OnStarting(() => { http.Response.Headers["X-Correlation-ID"] = correlation; return Task.CompletedTask; });
        foreach (var name in http.Request.Headers.Keys.Where(ReservedHeader).ToArray()) http.Request.Headers.Remove(name);
        var watch = Stopwatch.StartNew();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation });
        try
        {
            // Local HTTP is useful for test fixtures; real ingress must establish HTTPS.
            if (!http.Request.IsHttps && !http.Request.Path.StartsWithSegments("/health") &&
                (http.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(http.Connection.RemoteIpAddress)))
            { await GatewayProblems.WriteAsync(http, 403, "TLS_REQUIRED", "HTTPS is required."); return; }
            await next(http);
            var error = http.Features.Get<IForwarderErrorFeature>();
            if (error is not null)
            {
                if (http.RequestAborted.IsCancellationRequested) return;
                var (status, code) = Classify(error, http.Items.ContainsKey("GatewayDeadline"));
                await GatewayProblems.WriteAsync(http, status, code, "The service could not complete the request.");
            }
            else if (http.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is null && http.Response.StatusCode >= 400 && !http.Response.HasStarted && http.Response.ContentLength is null or 0 && string.IsNullOrEmpty(http.Response.ContentType))
                await GatewayProblems.WriteAsync(http, http.Response.StatusCode, "HTTP_ERROR", "The request could not be processed.");
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { http.Abort(); }
        catch (Exception exception)
        {
            logger.LogError("Gateway request failed with {ExceptionType}", exception.GetType().Name);
            if (!http.RequestAborted.IsCancellationRequested) await GatewayProblems.WriteAsync(http, 500, "GATEWAY_FAILURE", "The request could not be processed.");
        }
        finally
        {
            var route = http.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config;
            logger.LogInformation("{Method} {RouteId} {Service} returned {StatusCode} in {ElapsedMs} ms", http.Request.Method, route?.RouteId ?? "unmatched", route?.ClusterId ?? "gateway", http.Response.StatusCode, watch.ElapsedMilliseconds);
        }
    }

    public static (int Status, string Code) Classify(IForwarderErrorFeature error, bool deadline)
    {
        if (deadline || error.Error == ForwarderError.RequestTimedOut) return (504, "UPSTREAM_TIMEOUT");
        if (error.Error == ForwarderError.NoAvailableDestinations) return (503, "UPSTREAM_UNAVAILABLE");
        for (var exception = error.Exception; exception is not null; exception = exception.InnerException)
            if (exception is BadHttpRequestException badRequest) return (badRequest.StatusCode, badRequest.StatusCode == 413 ? "REQUEST_TOO_LARGE" : "INVALID_REQUEST");
        return (502, "UPSTREAM_FAILURE");
    }

    private static bool ReservedHeader(string name) => name.StartsWith("X-User-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Actor-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Identity-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-School-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Membership-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Permission-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || name.Equals("X-Http-Method-Override", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Http-Method", StringComparison.OrdinalIgnoreCase) || name.Equals("X-Method-Override", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex CorrelationPattern();
}
