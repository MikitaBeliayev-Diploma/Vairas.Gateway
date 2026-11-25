using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Vairas.Gateway;

public static class GatewayProblems
{
    public static Task WriteAsync(HttpContext http, int status, string code, string title)
    {
        if (http.Response.HasStarted) { http.Abort(); return Task.CompletedTask; }
        http.Response.StatusCode = status;
        http.Response.ContentLength = null;
        return http.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = title, Type = "about:blank", Instance = http.Request.Path,
            Extensions = { ["code"] = code, ["correlationId"] = http.Items["CorrelationId"], ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier }
        }, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
    }
}
