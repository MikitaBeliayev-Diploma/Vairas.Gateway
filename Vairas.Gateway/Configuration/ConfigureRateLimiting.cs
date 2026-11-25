using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

public static class ConfigureRateLimiting
{
    public static IServiceCollection AddIngressRateLimiting(this IServiceCollection services, GatewayOptions settings)
    {
        services.AddRateLimiter(options => IngressLimiter.Configure(options, settings));
        return services;
    }
}
