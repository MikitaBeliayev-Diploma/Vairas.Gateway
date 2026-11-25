using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

public static class ConfigureCors
{
    public static IServiceCollection AddBrowserCors(this IServiceCollection services, GatewayOptions settings)
    {
            services.AddCors(o => o.AddPolicy("browser", policy =>
            {
                if (settings.BrowserOrigins.Length > 0)
                    policy.WithOrigins(settings.BrowserOrigins).WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                        .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-ID")
                        .WithExposedHeaders("Content-Disposition", "Location", "Retry-After", "X-Correlation-ID", "WWW-Authenticate").AllowCredentials();
            }));
        return services;
    }
}
