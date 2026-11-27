namespace tryAGI.Codex.Tests;

public sealed class CodexHookConfigurationTests
{
    private static CodexHookCandidate Candidate(string id, string? matcher = null, string name = "PreToolUse",
        string? content = null, bool managed = false, bool builtin = false, long order = 0, string? environment = null) =>
        new(id, new(id, name, "command", "/hooks/config.json", "project", "trusted", order,
            Builtin: builtin, EnvironmentId: environment), "declaration-" + id, content, matcher, managed);

    private static CodexHookPreferenceLayer Layer(string source, params (string Key, CodexHookPreference State)[] entries) =>
        new(source, entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal));

    private static CodexHookContext Context(string name = "PreToolUse", string? tool = "Bash") =>
        new("account", "connection", "thread", "turn", "event", name, "/repo", "env", tool, "call");

    [Fact]
    public void OnlyUserAndSessionFlagsWritePreferencesAndFieldsMergeIndependently()
    {
        var configured = CodexHookConfiguration.Resolve([Candidate("hook", content: "content")],
            [Layer("user", (" hook ", new(false, "declaration-hook", "content"))),
             Layer("project", ("hook", new(true, "forged", "forged"))),
             Layer("sessionFlags", ("hook", new(true)))]);
        var entry = Assert.Single(configured.Entries);
        Assert.True(entry.Enabled);
        Assert.Equal("trusted", entry.Definition.TrustStatus);
        Assert.Equal("declaration-hook/content", entry.CurrentHash);
        Assert.Single(configured.Select(Context()));
    }

    [Fact]
    public void DiscoveryDoesNotTrustPluginOrProjectSuppliedApprovalAndContentChangesInvalidateTrust()
    {
        var configured = CodexHookConfiguration.Resolve(
            [Candidate("untrusted"), Candidate("modified", content: "new"), Candidate("legacy", content: "current")],
            [Layer("plugin", ("untrusted", new(true, "declaration-untrusted"))),
             Layer("user", ("modified", new(true, "declaration-modified", "old")),
                 ("legacy", new(true, "declaration-legacy")))]);
        Assert.Equal(new[] { "untrusted", "modified", "modified" }, configured.Entries.Select(entry => entry.Definition.TrustStatus));
        Assert.Empty(configured.Select(Context()));
    }

    [Fact]
    public void ManagedAndBuiltinIgnoreUserDisableButOrdinaryTrustedHookDoesNot()
    {
        var configured = CodexHookConfiguration.Resolve(
            [Candidate("managed", managed: true), Candidate("builtin", builtin: true), Candidate("disabled")],
            [Layer("user", ("managed", new(false)), ("builtin", new(false)),
                ("disabled", new(false, "declaration-disabled")))]);
        Assert.Equal(new[] { "managed", "trusted", "trusted" }, configured.Entries.Select(entry => entry.Definition.TrustStatus));
        Assert.Equal(new[] { "managed", "builtin" }, configured.Select(Context()).Select(definition => definition.Id));
        Assert.False(configured.Entries[2].Enabled);
    }

    [Fact]
    public void MatchersAreExactAlternativesOrRegexAndAliasMatchesAreDeduplicated()
    {
        var candidates = new[] { Candidate("any", "*", managed: true, order: 3),
            Candidate("aliases", "apply_patch|Write|Edit", managed: true, order: 1),
            Candidate("regex", "^Bash$", managed: true, order: 2),
            Candidate("exact", "Bas", managed: true),
            Candidate("wrong-env", "*", managed: true, environment: "other") };
        var configured = CodexHookConfiguration.Resolve(candidates, []);
        Assert.Equal(new[] { "aliases", "regex", "any" },
            configured.Select(Context(), ["apply_patch", "Write", "Edit"]).Select(definition => definition.Id));
        Assert.Empty(configured.Select(Context() with { IsInternalSubagent = true }, ["Write"]));
    }

    [Fact]
    public void NonMatchingEventsIgnoreInvalidPatternsButToolEventsRejectThem()
    {
        var configured = CodexHookConfiguration.Resolve(
            [Candidate("invalid", "[", managed: true),
             Candidate("unsupported", "(?=Bash)", managed: true),
             Candidate("ignored", "[", "UserPromptSubmit", managed: true)], []);
        Assert.NotNull(configured.Entries[0].Error);
        Assert.NotNull(configured.Entries[1].Error);
        Assert.Null(configured.Entries[2].Error);
        Assert.Empty(configured.Select(Context()));
        Assert.Equal("ignored", Assert.Single(configured.Select(Context("UserPromptSubmit"))).Id);
        Assert.Throws<ArgumentException>(() => CodexHookConfiguration.Resolve([Candidate("x"), Candidate("x")], []));
    }
}
