using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

/// <summary>
/// Binds the gateway settings and public routes. Validation is eager (before any service is registered) because the proxy
/// routes, clusters and one authentication scheme per service are materialized from these values at registration time.
/// </summary>
public static class ConfigureOptions
{
    /// <summary>Adds the route table, the optional local override file, environment variables and the command line (in that order of precedence).</summary>
    public static ConfigurationManager AddGatewayConfigurationSources(this ConfigurationManager configuration, string[] args)
    {
        configuration.AddJsonFile("routes.json", optional: false, reloadOnChange: false);
        configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        // Keep environment and command-line configuration above the optional local file.
        configuration.AddEnvironmentVariables().AddCommandLine(args);
        return configuration;
    }

    public static (GatewayOptions Settings, PublicRoute[] Routes) BindAndValidate(IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new();
        var routes = configuration.GetSection("PublicRoutes").Get<PublicRoute[]>() ?? [];
        ProxyConfiguration.Validate(settings, routes, environment);
        return (settings, routes);
    }

    public static IServiceCollection AddGatewayOptions(this IServiceCollection services, GatewayOptions settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<IngressLimiter>();
        return services;
    }
}
