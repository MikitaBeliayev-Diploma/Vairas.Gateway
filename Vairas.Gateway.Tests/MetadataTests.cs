using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vairas.Gateway.Tests;

public sealed class MetadataTests(GatewayWorld world) : IClassFixture<GatewayWorld>
{
    [Fact]
    public async Task HTTPS_discovery_provides_keys_for_the_configured_user_issuer()
    {
        await using var gateway = await world.StartGatewayAsync();
        var options = gateway.App.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("user-management");
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(default);
        Assert.Equal(world.Issuer, configuration.Issuer);
        Assert.NotEmpty(configuration.SigningKeys);
        var validation = options.TokenValidationParameters.Clone(); validation.IssuerSigningKeys = configuration.SigningKeys;
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(world.Token(), validation, out _);
        Assert.True(principal.Identity?.IsAuthenticated);
    }
}
