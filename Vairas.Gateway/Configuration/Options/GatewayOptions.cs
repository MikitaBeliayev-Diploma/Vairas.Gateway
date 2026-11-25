using System.Net;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Vairas.Gateway.Configuration.Options;


public sealed class GatewayOptions
{
    public string Issuer { get; set; } = "";
    public string MetadataAddress { get; set; } = "";
    public string[] BrowserOrigins { get; set; } = [];
    public string[] TrustedProxies { get; set; } = [];
    public string[] ForwardedHosts { get; set; } = [];
    public int ApiTimeoutSeconds { get; set; } = 30;
    public int FileTimeoutSeconds { get; set; } = 120;
    public int IpRequestsPerMinute { get; set; } = 300;
    public int AuthenticationRequestsPerMinute { get; set; } = 20;
    public int UserRequestsPerMinute { get; set; } = 120;
    public Dictionary<string, ServiceDestination> Services { get; set; } = [];
}
