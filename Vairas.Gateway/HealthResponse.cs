using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Yarp.ReverseProxy.Transforms;

namespace Vairas.Gateway;


/// <summary>Health of the Gateway process and its validated startup configuration.</summary>
public sealed record HealthResponse(string Status);
