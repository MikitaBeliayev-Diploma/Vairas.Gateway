using Vairas.Gateway.Configuration;
using Vairas.Gateway.Security;

namespace Vairas.Gateway;

/// <summary>Composition root. Tests build the same host through <see cref="Build"/> and may adjust the builder first.</summary>
public static class GatewayApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddGatewayConfigurationSources(args);
        configure?.Invoke(builder);
        var (settings, routes) = ConfigureOptions.BindAndValidate(builder.Configuration, builder.Environment);

        builder.WebHost.AddHardenedKestrel();
        builder.Logging.AddJsonLogging();
        builder.Services
            .AddGatewayOptions(settings)
            .AddGatewayAuthentication(settings)
            .AddIngressRateLimiting(settings)
            .AddRouteTimeouts(settings)
            .AddBrowserCors(settings)
            .AddTrustedProxies(settings)
            .AddPublicReverseProxy(settings, routes)
            .AddOpenApiDocument()
            .AddServiceTelemetry(builder.Configuration);

        var app = builder.Build();
        // Empty trusted-proxy lists must never accidentally mean 'trust every sender'.
        if (settings.TrustedProxies.Length > 0) app.UseForwardedHeaders();
        app.UseMiddleware<RequestBoundary>();
        app.UseRouting();
        app.UseCors("browser");
        app.Use((http, next) => http.RequestServices.GetRequiredService<IngressLimiter>().InvokeAsync(http, next));
        app.Use(RouteAuthentication.AuthenticateAsync);
        app.Use((http, next) => Vairas.Delegation.ReadPreviewDelegation.EnforceAsync(http, next, http.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.ClusterId ?? "", false));
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseRequestTimeouts();
        app.Use(async (http, next) =>
        {
            var timeout = http.Features.Get<Microsoft.AspNetCore.Http.Timeouts.IHttpRequestTimeoutFeature>();
            var maxSize = http.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.MaxRequestBodySize;
            if (maxSize.HasValue && http.Request.ContentLength > maxSize)
            { await GatewayProblems.WriteAsync(http, 413, "REQUEST_TOO_LARGE", "The request body is too large."); return; }
            try { await next(http); }
            finally { if (timeout?.RequestTimeoutToken.IsCancellationRequested == true) http.Items["GatewayDeadline"] = true; }
        });
        app.MapGet("/health/live", () => TypedResults.Ok(new HealthResponse("Healthy"))).WithMetadata(new HealthEndpoint()).AllowAnonymous();
        app.MapGet("/health/ready", () => TypedResults.Ok(new HealthResponse("Healthy"))).WithMetadata(new HealthEndpoint()).AllowAnonymous();
        app.MapReverseProxy().RequireCors("browser");
        app.MapOpenApiDocument();
        return app;
    }
}
