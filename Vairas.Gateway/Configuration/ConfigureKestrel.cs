namespace Vairas.Gateway.Configuration;

public static class ConfigureKestrel
{
    public static IWebHostBuilder AddHardenedKestrel(this IWebHostBuilder webHost) =>
        webHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
}
