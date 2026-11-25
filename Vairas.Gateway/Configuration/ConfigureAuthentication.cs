using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Vairas.Gateway.Configuration.Options;

namespace Vairas.Gateway.Configuration;

/// <summary>One bearer scheme and one authorization policy per owner service, each bound to that service's user audience.</summary>
public static class ConfigureAuthentication
{
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services, GatewayOptions settings)
    {
        var authentication = services.AddAuthentication();
        services.AddAuthorizationBuilder();
        foreach (var (service, destination) in settings.Services)
        {
            authentication.AddJwtBearer(service, options =>
            {
                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.RefreshOnIssuerKeyNotFound = true;
                options.TokenValidationParameters = new()
                {
                    ValidIssuer = settings.Issuer, ValidateIssuer = true,
                    ValidAudience = destination.Audience, ValidateAudience = true,
                    ValidateLifetime = true, RequireExpirationTime = true,
                    RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub"
                };
                options.Events = new()
                {
                    OnTokenValidated = context =>
                    {
                        if (!ValidId(context.Principal, "sub") || !ValidId(context.Principal, "sid")) context.Fail("Invalid user binding.");
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return GatewayProblems.WriteAsync(context.HttpContext, 401, "AUTHENTICATION_REQUIRED", "Authentication required.");
                    },
                    OnForbidden = context => GatewayProblems.WriteAsync(context.HttpContext, 403, "ACCESS_DENIED", "Access denied.")
                };
            });
            services.AddAuthorizationBuilder().AddPolicy(service, policy => policy.AddAuthenticationSchemes(service).RequireAuthenticatedUser());
        }
        return services;
    }

    private static bool ValidId(ClaimsPrincipal? principal, string name) => principal?.FindAll(name).ToArray() is { Length: 1 } claims && Guid.TryParse(claims[0].Value, out var id) && id != Guid.Empty;
}
