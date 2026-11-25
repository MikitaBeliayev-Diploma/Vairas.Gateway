using System.Net;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Vairas.Gateway.Configuration.Options;


public sealed class ServiceDestination
{
    public string Address { get; set; } = "";
    public string Audience { get; set; } = "";
}
