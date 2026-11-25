using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway;


public sealed class IngressLimiter(GatewayOptions settings) : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => Window(settings.IpRequestsPerMinute)));

    public async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<HealthEndpoint>() is not null) { await next(http); return; }
        using var lease = await limiter.AcquireAsync(http, cancellationToken: http.RequestAborted);
        if (!lease.IsAcquired) { await RejectedAsync(http, lease); return; }
        await next(http);
    }

    public static void Configure(RateLimiterOptions options, GatewayOptions settings)
    {
        options.AddPolicy("authentication", http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => Window(settings.AuthenticationRequestsPerMinute)));
        options.AddPolicy("user", http => RateLimitPartition.GetFixedWindowLimiter(
            http.User.Identity?.IsAuthenticated == true ? "user:" + http.User.FindFirstValue("sub") : "ip:" + ClientIp(http), _ => Window(settings.UserRequestsPerMinute)));
        options.OnRejected = (context, _) => new ValueTask(RejectedAsync(context.HttpContext, context.Lease));
    }

    private static FixedWindowRateLimiterOptions Window(int limit) => new() { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };
    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private static Task RejectedAsync(HttpContext http, RateLimitLease lease)
    {
        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return GatewayProblems.WriteAsync(http, 429, "RATE_LIMITED", "Try again later.");
    }
    public void Dispose() => limiter.Dispose();
}
