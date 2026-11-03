using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace tryAGI.Codex.Tests;

/// <summary>
/// Pins the hand-maintained App Server models to the newest schema snapshot. A drift pull request that
/// renames or removes a member they declare, or makes a new parameter required, fails here.
/// </summary>
public sealed class AppServerModelSchemaTests
{
    public static TheoryData<string, string, bool> Models => new()
    {
        // Model type, schema definition, whether it is sent (all required members must then be declared).
        { nameof(CodexAppServerThreadStartParams), "ThreadStartParams", true },
        { nameof(CodexAppServerThreadStartResponse), "ThreadStartResponse", false },
        { nameof(CodexAppServerTurnStartParams), "TurnStartParams", true },
        { nameof(CodexAppServerTurnStartResponse), "TurnStartResponse", false },
        { nameof(CodexAppServerTurnInterruptParams), "TurnInterruptParams", true },
        { nameof(CodexAppServerEmpty), "TurnInterruptResponse", false },
        { "CodexAppServerThread", "Thread", false },
        { "CodexAppServerTurn", "Turn", false },
        { "CodexAppServerTurnError", "TurnError", false },
        { nameof(CodexAppServerTurnNotification), "TurnStartedNotification", false },
        { nameof(CodexAppServerTurnNotification), "TurnCompletedNotification", false },
        { nameof(CodexAppServerAgentMessageDelta), "AgentMessageDeltaNotification", false },
        { nameof(CodexAppServerErrorNotification), "ErrorNotification", false },
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void DeclaredMembersExistInTheNewestSchema(string model, string definition, bool sent)
    {
        var info = TypeInfo(model);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "specs", CodexAppServerProtocol.Version, "codex_app_server_protocol.schemas.json")));
        var type = schema.RootElement.GetProperty("definitions").GetProperty("v2").GetProperty(definition);
        var properties = type.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(x => x.Name).ToHashSet() : [];
        var required = type.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : [];
        // [JsonIgnore] members stay in the metadata without accessors; they are not on the wire.
        var declared = info.Properties.Where(x => !x.IsExtensionData && (x.Get is not null || x.Set is not null)).ToList();

        foreach (var member in declared)
            Assert.True(properties.Contains(member.Name), $"{model}.{member.Name} is not in {definition} of App Server {CodexAppServerProtocol.Version}.");
        if (sent)
        {
            foreach (var name in required)
                Assert.True(declared.Any(x => x.Name == name), $"{definition} requires {name}, which {model} does not declare.");
        }
        else
        {
            // A non-nullable member the server may omit would silently read as null.
            foreach (var member in declared.Where(x => !x.IsGetNullable))
                Assert.True(required.Contains(member.Name), $"{model}.{member.Name} is non-nullable but optional in {definition}.");
        }
        Assert.True(info.Properties.Count(x => x.IsExtensionData) == 1, $"{model} must keep undeclared members.");
    }

    private static JsonTypeInfo TypeInfo(string model) =>
        CodexAppServerJson.Default.GetTypeInfo(typeof(CodexAppServerJson).Assembly.GetType("tryAGI.Codex." + model)
            ?? throw new InvalidOperationException(model)) ?? throw new InvalidOperationException(model);
}
