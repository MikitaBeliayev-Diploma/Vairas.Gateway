using System.Text.Json;
using System.Text.RegularExpressions;
using Vairas.Gateway.Configuration.Options;
using Xunit;

namespace Vairas.Gateway.Tests;

/// <summary>
/// Every public route must be an operation that its owner service actually declares in its exported OpenAPI contract, so a
/// route can neither point at a missing operation nor silently expose one the owner does not publish. This replaces a fixed
/// route-count assertion, which broke on every legitimate route addition without proving anything about the new route.
/// The owner contracts live in the sibling service checkouts (Backend/Vairas.*/api); a missing contract fails the test.
/// </summary>
public sealed class RouteContractTests(GatewayWorld world) : IClassFixture<GatewayWorld>
{
    private static readonly Dictionary<string, string> OwnerFolders = new(StringComparer.Ordinal)
    {
        ["authentication"] = "Vairas.Authentication", ["user-management"] = "Vairas.UserManagement", ["school-management"] = "Vairas.SchoolManagement",
        ["scheduling"] = "Vairas.Scheduling", ["vehicle-management"] = "Vairas.VehicleManagement", ["learning-tracking"] = "Vairas.LearningTracking",
        ["notifications"] = "Vairas.Notifications", ["document-management"] = "Vairas.DocumentManagement", ["chat"] = "Vairas.ChatBackend"
    };

    private static string BackendRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Vairas.Gateway")) && Directory.Exists(Path.Combine(dir.FullName, "Vairas.Scheduling")))
                return dir.FullName;
        throw new InvalidOperationException("The Backend checkout with all service directories was not found.");
    }

    private static string Normalize(string path) => Regex.Replace(path, @"\{[^}]*\}", "{}");

    private static HashSet<(string Method, string Path)> Operations(string ownerFolder)
    {
        var directory = Path.Combine(BackendRoot(), ownerFolder, "api");
        var files = Directory.GetFiles(directory, "*.openapi.json");
        Assert.True(files.Length > 0, $"No OpenAPI contract found for {ownerFolder}.");
        var operations = new HashSet<(string, string)>();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
                foreach (var method in path.Value.EnumerateObject())
                    if (method.Name is "get" or "post" or "put" or "patch" or "delete")
                        operations.Add((method.Name.ToUpperInvariant(), Normalize(path.Name)));
        }
        return operations;
    }

    [Fact]
    public void Every_public_route_is_an_operation_declared_in_its_owners_OpenAPI_contract()
    {
        Assert.NotEmpty(world.Routes);
        var contracts = new Dictionary<string, HashSet<(string Method, string Path)>>();
        var missing = new List<string>();
        foreach (var route in world.Routes)
        {
            Assert.True(OwnerFolders.TryGetValue(route.Service, out var folder), $"Unknown owner service {route.Service} for route {route.Id}.");
            if (!contracts.TryGetValue(folder!, out var operations)) contracts[folder!] = operations = Operations(folder!);
            if (!operations.Contains((route.Method, Normalize(route.Path)))) missing.Add($"{route.Id}: {route.Method} {route.Path}");
        }
        Assert.True(missing.Count == 0, "Routes not declared by their owner's OpenAPI contract:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Route_identifiers_and_operations_are_unique()
    {
        Assert.Equal(world.Routes.Length, world.Routes.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(world.Routes.Length, world.Routes.Select(r => r.Method + " " + Normalize(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
