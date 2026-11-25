using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

public static class ConfigureRequestTimeouts
{
    public static IServiceCollection AddRouteTimeouts(this IServiceCollection services, GatewayOptions settings)
    {
            services.AddRequestTimeouts(o =>
            {
                foreach (var (name, seconds) in new[] { ("api", settings.ApiTimeoutSeconds), ("files", settings.FileTimeoutSeconds), ("chat", 75) })
                    o.AddPolicy(name, new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
                    {
                        Timeout = TimeSpan.FromSeconds(seconds), TimeoutStatusCode = 504,
                        WriteTimeoutResponse = http => GatewayProblems.WriteAsync(http, 504, "UPSTREAM_TIMEOUT", "The service did not respond in time.")
                    });
            });
        return services;
    }
}
