using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

public static class ConfigureForwardedHeaders
{
    /// <summary>Forwarded headers are trusted only from explicitly listed proxies; an empty list never means "trust every sender".</summary>
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, GatewayOptions settings)
    {
            if (settings.TrustedProxies.Length > 0)
                services.Configure<ForwardedHeadersOptions>(o =>
                {
                    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                    o.ForwardLimit = 1;
                    o.KnownProxies.Clear(); o.KnownIPNetworks.Clear();
                    foreach (var proxy in settings.TrustedProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
                    foreach (var host in settings.ForwardedHosts) o.AllowedHosts.Add(host);
                });
        return services;
    }
}
