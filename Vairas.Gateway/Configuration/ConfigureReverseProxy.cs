using Yarp.ReverseProxy.Transforms;
using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

public static class ConfigureReverseProxy
{
    public static IServiceCollection AddPublicReverseProxy(this IServiceCollection services, GatewayOptions settings, PublicRoute[] routes)
    {
            services.AddReverseProxy().LoadFromMemory(routes.Select(ProxyConfiguration.ToRoute).ToArray(), settings.Services.Select(s => ProxyConfiguration.ToCluster(s.Key, s.Value)).ToArray())
                .AddTransforms(transform => transform.AddResponseTransform(context =>
                {
                    // The browser sees one CORS policy even if an owner also supports direct access.
                    foreach (var header in new[] { "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials", "Access-Control-Allow-Headers", "Access-Control-Allow-Methods", "Access-Control-Expose-Headers", "Access-Control-Max-Age" })
                        context.HttpContext.Response.Headers.Remove(header);
                    return ValueTask.CompletedTask;
                }));
        return services;
    }
}
