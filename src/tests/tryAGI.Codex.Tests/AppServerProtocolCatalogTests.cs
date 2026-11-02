using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class AppServerProtocolCatalogTests
{
    private static readonly string SpecsDirectory = Path.Combine(AppContext.BaseDirectory, "specs");

    [Fact]
    public void CatalogMatchesNewestSchemaSnapshot()
    {
        var newest = Directory.GetDirectories(SpecsDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .MaxBy(Version.Parse);
        Assert.Equal(newest, CodexAppServerProtocol.Version);

        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SpecsDirectory, CodexAppServerProtocol.Version, "codex_app_server_protocol.schemas.json")));
        var definitions = schema.RootElement.GetProperty("definitions");
        Assert.Equal(Methods(definitions, "ClientRequest"), CodexAppServerProtocol.ClientRequests);
        Assert.Equal(Methods(definitions, "ClientNotification"), CodexAppServerProtocol.ClientNotifications);
        Assert.Equal(Methods(definitions, "ServerRequest"), CodexAppServerProtocol.ServerRequests);
        Assert.Equal(Methods(definitions, "ServerNotification"), CodexAppServerProtocol.ServerNotifications);
    }

    [Fact]
    public void EveryServedAndSentMethodExistsInTheCatalog()
    {
        // Counting through the predicates keeps one source of truth: a method removed or renamed
        // upstream drops the count and fails the drift pull request instead of merging it.
        Assert.Equal(12, CodexAppServerProtocol.ClientRequests.Count(CodexThreadRpc.Handles));
        Assert.Equal(9, CodexAppServerProtocol.ClientRequests.Count(CodexFileSystem.Handles));
        Assert.True(CodexAppServerProtocol.IsClientRequest("initialize"));
        Assert.Contains("initialized", CodexAppServerProtocol.ClientNotifications);
        Assert.Contains("turn/completed", CodexAppServerProtocol.ServerNotifications);
        Assert.Contains("fs/changed", CodexAppServerProtocol.ServerNotifications);
        Assert.False(CodexAppServerProtocol.IsClientRequest("no/such/method"));
    }

    private static string[] Methods(JsonElement definitions, string union) =>
        definitions.GetProperty(union).GetProperty("oneOf").EnumerateArray()
            .SelectMany(variant => variant.GetProperty("properties").GetProperty("method").GetProperty("enum").EnumerateArray())
            .Select(method => method.GetString() ?? "")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
