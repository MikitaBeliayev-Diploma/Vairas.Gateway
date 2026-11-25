# Vairas Gateway

Single public entry point (ASP.NET Core 10 + YARP reverse proxy). It forwards only an explicit allowlist of public
routes (`routes.json`) to the nine owner services, authenticates the caller with the exact user audience of the
target service, applies per-IP and per-user rate limits, route timeouts and body limits, and exposes one CORS policy
for the configured browser origins. It owns no data, issues no tokens and never exposes internal or MCP routes.

## Projects

| Project | Responsibility |
|---|---|
| `Vairas.Gateway` | Host: `GatewayApplication` composition root, `Configuration/Configure*`, route/cluster mapping (`ProxyConfiguration`), ingress limiter, request boundary |
| `Vairas.Gateway.Tests` | Route table, delegation policy, proxy and boundary tests |
| `Vairas.Gateway.ServiceTests` | HTTP scenarios against the real owner services |

## Dependencies

Authentication (issuer metadata and JWKS) and the nine owner services behind `Gateway:Services`. Local origin
`https://localhost:7000`.

## Essential configuration

Settings and routes are bound and validated before any service is registered (`Configuration/ConfigureOptions.cs`),
because proxy routes, clusters and one authentication scheme per service are created from them. Use `__` in
environment variables.

| Section | Notes |
|---|---|
| `Gateway:Issuer`, `Gateway:MetadataAddress` | HTTPS; issuer without trailing slash |
| `Gateway:Services` | exactly the nine services; each `Address` (HTTPS, loopback HTTP only in Development/Testing) and `Audience` `vairas-<service>` |
| `Gateway:BrowserOrigins` | exact HTTPS origins (loopback HTTP only in Development) |
| `Gateway:TrustedProxies`, `ForwardedHosts` | explicit IPs/hosts; empty proxy list trusts no forwarded headers |
| `Gateway` limits | request-per-minute and timeout bounds |
| `PublicRoutes` (`routes.json`) | the public route allowlist; validated for ambiguity, anonymous and file-transfer rules |

## Run locally

```powershell
dotnet run --project Vairas.Gateway --launch-profile https
```

The complete local stack is started from `../local/Start.ps1`; the Gateway is `https://localhost:7000`.

## Tests

```powershell
dotnet test Vairas.Gateway.slnx
./scripts/verify.ps1 -SkipIntegration           # tests with the coverage gate
```

API exports: `api/`. Extended notes, validation reports and evidence: `../../Documentation/Backend/Vairas.Gateway/`.
