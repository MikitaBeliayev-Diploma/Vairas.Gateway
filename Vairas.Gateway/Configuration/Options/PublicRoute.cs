using System.Net;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Vairas.Gateway.Configuration.Options;


public sealed record PublicRoute(string Id, string Service, string Method, string Path, bool Anonymous = false, bool FileTransfer = false, bool Upload = false);
