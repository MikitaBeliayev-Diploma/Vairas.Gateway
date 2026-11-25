using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Vairas.Gateway.Configuration.Options;
using Vairas.Gateway.Configuration;

namespace Vairas.Gateway.Tests;

public sealed record GatewayHandle(WebApplication App, HttpClient Client) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
}
