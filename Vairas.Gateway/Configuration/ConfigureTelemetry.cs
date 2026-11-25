using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Vairas.Gateway.Configuration;

public static class ConfigureTelemetry
{
    public static IServiceCollection AddServiceTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var exportToCollector = !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("vairas-gateway"))
            .WithTracing(tracing => { tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(); if (exportToCollector) tracing.AddOtlpExporter(); })
            .WithMetrics(metrics => { metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(); if (exportToCollector) metrics.AddOtlpExporter(); });
        return services;
    }
}
