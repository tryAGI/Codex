using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexPluginManifestTests
{
    private static readonly CodexSkillPath Root = new("host", "/plugins/example");

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ParsesResourceVariantsAndRetainsRawMetadataWithoutResolvingOrLoading()
    {
        var raw = Json("""
            {"name":"example","version":" 1.2.3 ","description":"Description","keywords":["one","two"],
             "skills":["./skills","./more"],"mcpServers":{"remote":{"url":"https://example.test/mcp"}},
             "apps":"./.apps.json","hooks":[{"hooks":{}},{"future":true}],
             "extensions":{"com.openai":{"onboardingSkill":"skills/onboarding/SKILL.md"}},
             "interface":{"displayName":" Example UI ","websiteURL":"https://example.test",
               "defaultPrompt":["  First\n prompt ",42,"", "Second","Third","Fourth"],
               "logoDark":"./assets/dark.svg","screenshots":["./assets/a.png"]},"future":{"raw":true}}
            """);
        var parsed = CodexPluginManifest.Parse(Root, "fallback", raw);
        Assert.Equal("example", parsed.Name);
        Assert.Equal("Example UI", parsed.DisplayName);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.Equal(new[] { "one", "two" }, parsed.Keywords);
        Assert.Equal(new[] { "skills", "more" }, parsed.Skills.Select(item => item.RelativePath));
        Assert.Equal(Root, parsed.Skills[0].Root);
        Assert.Equal("skills/onboarding/SKILL.md", parsed.OnboardingSkill!.RelativePath);
        Assert.Equal(".apps.json", parsed.Apps!.RelativePath);
        Assert.Equal("https://example.test/mcp", parsed.McpServers!.Inline!.Value.GetProperty("remote").GetProperty("url").GetString());
        Assert.Equal(2, parsed.Hooks!.Inline.Count);
        Assert.Equal("assets/dark.svg", parsed.Interface!.LogoDark!.RelativePath);
        Assert.Equal("https://example.test", parsed.Interface.WebsiteUrl);
        Assert.Equal(new[] { "First prompt", "Second", "Third" }, parsed.Interface.DefaultPrompts);
        Assert.True(parsed.Raw.GetProperty("future").GetProperty("raw").GetBoolean());
        Assert.Equal(3, parsed.Warnings.Count);
    }

    [Fact]
    public void ResourcePathsCannotEscapeRootAndWindowsSeparatorsAreExplicit()
    {
        var raw = Json("""
            {"skills":["./safe","../escape","./../escape",".//root","./C:/bad","./","./safe/../bad"],
             "mcpServers":"./mcp.json","apps":"./../apps.json","hooks":"./hooks.json",
             "interface":{"logo":"./assets/../outside.png"}}
            """);
        var parsed = CodexPluginManifest.Parse(Root, "fallback", raw);
        Assert.Equal("fallback", parsed.Name);
        Assert.Equal("safe", Assert.Single(parsed.Skills).RelativePath);
        Assert.Equal("mcp.json", parsed.McpServers!.Resource!.RelativePath);
        Assert.Null(parsed.Apps);
        Assert.Equal("hooks.json", Assert.Single(parsed.Hooks!.Resources).RelativePath);
        Assert.Null(parsed.Interface!.Logo);
        Assert.Equal(8, parsed.Warnings.Count);
        var win = Json("""{"skills":["./folder\\skill","./folder\\..\\outside","./D:\\bad","./folder\\.. \\outside","./invalid."]}""");
        Assert.Equal("folder/skill", Assert.Single(CodexPluginManifest.Parse(
            new("windows", "C:\\plugins\\example"), "fallback", win, CodexPluginPathConvention.Windows).Skills).RelativePath);
        Assert.Equal("invalid.", Assert.Single(CodexPluginManifest.Parse(Root, "fallback", win).Skills).RelativePath);
    }

    [Fact]
    public void InvalidOptionalResourceShapesAreIgnoredAndDuplicateFieldsRejected()
    {
        var parsed = CodexPluginManifest.Parse(Root, "fallback", Json("""{"skills":[42],"mcpServers":42,"hooks":[true],"interface":{"defaultPrompt":false}}"""));
        Assert.Empty(parsed.Skills);
        Assert.Null(parsed.McpServers);
        Assert.Null(parsed.Hooks);
        Assert.Empty(parsed.Interface!.DefaultPrompts);
        Assert.Equal(4, parsed.Warnings.Count);
        Assert.Throws<CodexProtocolException>(() => CodexPluginManifest.Parse(Root, "fallback",
            Json("""{"name":"one","name":"two"}""")));
        Assert.Throws<CodexProtocolException>(() => CodexPluginManifest.Parse(Root, "fallback",
            Json("""{"name":42}""")));
    }
}

public sealed class CodexPluginSuggestionTests
{
    private static CodexPluginDiscoveryPolicy Policy(bool remote = true, string? client = null) =>
        new(true, remote, true, ["configured@local", "remote-configured"], ["blocked@local", "remote-disabled"],
            ["remote-installed"], ["loaded-app"], client);

    private static CodexPluginDiscoveryCandidate Plugin(string id, string? remote = null, bool installed = false,
        bool allowed = true, bool adminDisabled = false, params string[] connectors) =>
        new(id, id, null, remote, installed, allowed, adminDisabled, true, ["mcp"], connectors);

    [Fact]
    public void FiltersLocalAndRemoteCandidatesWithoutInstallingOrConnecting()
    {
        var plugins = new[]
        {
            Plugin("configured@local"), Plugin("github@openai-curated"), Plugin("notion@openai-api-curated"),
            Plugin("blocked@local"), Plugin("unknown@local"), Plugin("gmail@openai-curated", installed: true),
            Plugin("slack@openai-curated", allowed: false), Plugin("chrome@openai-bundled", adminDisabled: true),
            Plugin("custom@remote", "remote-configured"), Plugin("other@remote", "other-id", connectors: ["loaded-app"]),
            Plugin("github@openai-curated-remote", "remote-installed"),
            Plugin("slack@openai-curated-remote", "remote-disabled"),
            Plugin("unknown@remote", "unknown-id"), Plugin("figma@openai-curated-remote", "remote-figma"),
        };
        var connectors = new[]
        {
            new CodexConnectorDiscoveryCandidate("new-connector", "new-connector", null, "https://example.test/install", false, true),
            new CodexConnectorDiscoveryCandidate("accessible", "accessible", null, null, true, true),
            new CodexConnectorDiscoveryCandidate("ineligible", "ineligible", null, null, false, false),
        };
        var catalog = CodexPluginSuggestionCatalog.Discover(Policy(), plugins, connectors);
        Assert.Equal(new[] { "configured@local", "custom@remote", "figma@openai-curated-remote", "github@openai-curated", "new-connector",
            "notion@openai-api-curated", "other@remote" }, catalog.Suggestions.Select(item => item.Id));
        var local = CodexPluginSuggestionCatalog.Discover(Policy(remote: false), plugins, connectors);
        Assert.DoesNotContain(local.Suggestions, item => item.RemotePluginId is not null);
        var tui = CodexPluginSuggestionCatalog.Discover(Policy(client: "codex-tui"), plugins, connectors);
        Assert.Equal(CodexPluginSuggestionKind.Connector, Assert.Single(tui.Suggestions).Kind);
    }

    [Fact]
    public void InstallFormUsesExactCatalogMetadataAndConfirmationIsNotCompletion()
    {
        var catalog = CodexPluginSuggestionCatalog.Discover(Policy(),
            [Plugin("custom@remote", "remote-configured", connectors: ["app-a", "app-b"]), Plugin("configured@local")],
            [new("connector", "Connector", null, "https://example.test/install", false, true)]);
        var suggestion = catalog.PrepareInstall(CodexPluginSuggestionKind.Plugin, "custom@remote", "install", "Need this tool", "host-id");
        var meta = suggestion.Elicitation.GetProperty("_meta");
        Assert.Equal("form", suggestion.Elicitation.GetProperty("mode").GetString());
        Assert.Equal("tool_suggestion", meta.GetProperty("codex_approval_kind").GetString());
        Assert.Equal("always", meta.GetProperty("persist").GetString());
        Assert.Equal("host-id", meta.GetProperty("suggestion_id").GetString());
        Assert.Equal("remote-configured", meta.GetProperty("remote_plugin_id").GetString());
        Assert.Equal(new[] { "app-a", "app-b" }, meta.GetProperty("app_connector_ids").EnumerateArray().Select(item => item.GetString()));
        Assert.False(catalog.VerifyInstall(suggestion, true, [], ["app-a"]));
        Assert.False(catalog.VerifyInstall(suggestion, false, [], ["app-a", "app-b"]));
        Assert.True(catalog.VerifyInstall(suggestion, true, [], ["app-a", "app-b"]));
        var local = catalog.PrepareInstall(CodexPluginSuggestionKind.Plugin, "configured@local", "install", "Need it", "local-id");
        Assert.False(catalog.VerifyInstall(local, true, [], []));
        Assert.True(catalog.VerifyInstall(local, true, ["configured@local"], []));
        var connector = catalog.PrepareInstall(CodexPluginSuggestionKind.Connector, "connector", "install", "Need it", "connector-id");
        Assert.Equal("https://example.test/install", connector.Elicitation.GetProperty("_meta").GetProperty("install_url").GetString());
        Assert.False(connector.Elicitation.GetProperty("_meta").TryGetProperty("suggestion_id", out _));
        Assert.False(catalog.VerifyInstall(connector, true, [], []));
        Assert.True(catalog.VerifyInstall(connector, true, [], ["connector"]));
    }

    [Fact]
    public void RemotePluginWithoutConnectorsRequiresInstallationEvidence()
    {
        var catalog = CodexPluginSuggestionCatalog.Discover(Policy(),
            [Plugin("custom@remote", "remote-configured")], []);
        var suggestion = catalog.PrepareInstall(CodexPluginSuggestionKind.Plugin, "custom@remote", "install", "Need it", "host-id");
        Assert.False(catalog.VerifyInstall(suggestion, true, [], []));
        Assert.True(catalog.VerifyInstall(suggestion, true, ["remote-configured"], []));
    }

    [Fact]
    public void UnknownDuplicateOrUnsupportedActionsFailClosed()
    {
        var plugin = Plugin("configured@local");
        Assert.Throws<CodexProtocolException>(() => CodexPluginSuggestionCatalog.Discover(Policy(), [plugin, plugin], []));
        var catalog = CodexPluginSuggestionCatalog.Discover(Policy(), [plugin], []);
        Assert.Throws<UnauthorizedAccessException>(() =>
            catalog.PrepareInstall(CodexPluginSuggestionKind.Plugin, "unknown", "install", "reason", "id"));
        Assert.Throws<CodexProtocolException>(() =>
            catalog.PrepareInstall(CodexPluginSuggestionKind.Plugin, plugin.Id, "enable", "reason", "id"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            catalog.PrepareInstall(CodexPluginSuggestionKind.Connector, plugin.Id, "install", "reason", "id"));
    }
}
