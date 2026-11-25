namespace Vairas.Gateway.Security;

public static class RouteAuthentication
{
    // Authentication runs before the rate limiter; authorization will reuse its cached result.
    public static async Task AuthenticateAsync(HttpContext http, RequestDelegate next)
    {
        var service = http.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.ClusterId;
        if (service is not null && http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null)
        {
            var result = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(http, service);
            if (result.Succeeded && result.Principal is not null) http.User = result.Principal;
        }
        await next(http);
    }
}
