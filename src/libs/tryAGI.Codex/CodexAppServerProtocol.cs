using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>
/// The method catalog of the Codex App Server protocol version this package was built against,
/// generated from the version's JSON schema.
/// </summary>
public static class CodexAppServerProtocol
{
    private static readonly JsonElement Catalog = Read();

    /// <summary>The App Server protocol version, for example <c>0.165.0</c>.</summary>
    public static string Version { get; } = Catalog.GetProperty("version").GetString()
        ?? throw new InvalidDataException("App Server catalog has no version.");

    /// <summary>Requests a client may send (<c>thread/start</c>, <c>turn/start</c>, …).</summary>
    public static IReadOnlyList<string> ClientRequests { get; } = Methods("clientRequests");

    /// <summary>Notifications a client may send (<c>initialized</c>).</summary>
    public static IReadOnlyList<string> ClientNotifications { get; } = Methods("clientNotifications");

    /// <summary>Requests the server may send and expects answered (approvals, user input, …).</summary>
    public static IReadOnlyList<string> ServerRequests { get; } = Methods("serverRequests");

    /// <summary>Notifications the server may send (<c>turn/completed</c>, <c>item/agentMessage/delta</c>, …).</summary>
    public static IReadOnlyList<string> ServerNotifications { get; } = Methods("serverNotifications");

    /// <summary>Returns <see langword="true"/> when <paramref name="method"/> is a client request of this version.</summary>
    public static bool IsClientRequest(string method) => ClientRequests.Contains(method, StringComparer.Ordinal);

    private static IReadOnlyList<string> Methods(string key) => Array.AsReadOnly(Catalog.GetProperty(key).EnumerateArray()
        .Select(x => x.GetString() ?? throw new InvalidDataException("App Server catalog contains a null method.")).ToArray());

    private static JsonElement Read()
    {
        using var stream = typeof(CodexAppServerProtocol).Assembly.GetManifestResourceStream("tryAGI.Codex.AppServerProtocol.json")
            ?? throw new InvalidOperationException("App Server protocol catalog is unavailable.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}
