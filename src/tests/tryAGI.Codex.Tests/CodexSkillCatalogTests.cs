using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexSkillCatalogTests
{
    private static readonly CodexSkillDiscoveryContext Context = new("account", "connection", "/repo",
        "grants-1", "config-1", "thread", "env");
    private static CodexSkillRoot Root(string path, string fs = "host", bool enabled = true) =>
        new(new(fs, path), CodexSkillScope.Repo, Enabled: enabled);
    private static CodexSkillDocument Skill(string name, string path, string? alias = null, bool implicitAllowed = true) =>
        new(name, "Description", path, alias, AllowImplicitInvocation: implicitAllowed);
    private static CodexSkillRootInventory Inventory(params CodexSkillDocument[] documents) => new(documents, []);
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task RootOrderDeduplicationAndDisabledRootsDoNotLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var roots = new[] { Root("/first"), Root("/disabled", enabled: false), Root("/first"), Root("/second") };
        var loaded = new List<string>();
        Task<CodexSkillRootInventory> Load(CodexSkillLoadRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            loaded.Add(request.Root.Identity.Path);
            return Task.FromResult(request.Root.Identity.Path == "/first"
                ? Inventory(Skill("alpha", "/a/SKILL.md"), Skill("beta", "/b/SKILL.md"))
                : Inventory(Skill("duplicate", "/a/SKILL.md"), Skill("gamma", "/c/SKILL.md")));
        }
        var snapshot = await catalog.DiscoverAsync(Context, roots, [], Load, ct);
        Assert.Equal(new[] { "/first", "/second" }, loaded);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, snapshot.Entries.Select(entry => entry.Document.Name));
        Assert.Same(snapshot, await catalog.DiscoverAsync(Context, roots, [], Load, ct));
        Assert.Equal(2, loaded.Count);
        var newContext = Context with { AccessKey = "grants-2" };
        Assert.NotSame(snapshot, await catalog.DiscoverAsync(newContext, roots, [], Load, ct));
        Assert.Equal(4, loaded.Count);
    }

    [Fact]
    public async Task StructuredAndLinkedPathsWinOverPlainNamesInRustOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root")], [],
            (_, _) => Task.FromResult(Inventory(
                Skill("alpha", "/a/SKILL.md", "/alias/SKILL.md"), Skill("beta", "/b/SKILL.md"),
                Skill("gamma", "/c/SKILL.md"))), ct);
        var selected = snapshot.Select([new CodexTextInput("$beta $alpha [$gamma](skill:///c/SKILL.md)"),
            new CodexSkillInput("alpha", "/alias/SKILL.md")]);
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, selected.Select(entry => entry.Document.Name));
    }

    [Fact]
    public async Task AmbiguousNamesAndConnectorConflictsRequireExactPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root")], [],
            (_, _) => Task.FromResult(Inventory(
                Skill("dup", "/one/SKILL.md"), Skill("dup", "/two/SKILL.md"),
                Skill("calendar", "/cal/SKILL.md"), Skill("PATH", "/path/SKILL.md"))), ct);
        var connectors = new Dictionary<string, int> { ["calendar"] = 1 };
        Assert.Empty(snapshot.Select([new CodexTextInput("$dup $calendar $PATH")], connectors));
        Assert.Empty(snapshot.Select([new CodexTextInput("[$calendar](app://calendar) [$dup](plugin://dup)")], connectors));
        Assert.Equal(new[] { "/two/SKILL.md", "/cal/SKILL.md" },
            snapshot.Select([new CodexTextInput("[$dup](/two/SKILL.md) [$calendar](/cal/SKILL.md)")], connectors)
                .Select(entry => entry.Identity.Path));
    }

    [Fact]
    public async Task DisabledAndMissingStructuredSelectionNeverFallsBackByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root")], [new("host", "/disabled/SKILL.md")],
            (_, _) => Task.FromResult(Inventory(Skill("alpha", "/enabled/SKILL.md"),
                Skill("alpha", "/disabled/SKILL.md", "/alias/SKILL.md"))), ct);
        Assert.Single(snapshot.Select([new CodexTextInput("$alpha")]));
        Assert.Empty(snapshot.Select([new CodexSkillInput("alpha", "/missing/SKILL.md"), new CodexTextInput("$alpha")]));
        Assert.Empty(snapshot.Select([new CodexSkillInput("alpha", "/alias/SKILL.md"), new CodexTextInput("$alpha")]));
        Assert.Empty(snapshot.Select([new CodexTextInput("[$alpha](/missing/SKILL.md)")]));
    }

    [Fact]
    public async Task SamePathDifferentFilesystemsCannotAliasWithoutExplicitResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root", "host"), Root("/root", "executor")], [],
            (_, _) => Task.FromResult(Inventory(Skill("shared", "/same/SKILL.md"))), ct);
        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Empty(snapshot.Select([new CodexSkillInput("shared", "/same/SKILL.md")]));
        var selected = Assert.Single(snapshot.Select([new CodexSkillInput("shared", "file:///same/SKILL.md")],
            resolvePath: _ => new CodexSkillPath("executor", "/same/SKILL.md")));
        Assert.Equal("executor", selected.Identity.FileSystemId);
        Assert.Empty(snapshot.Select([new CodexSkillInput("shared", "/same/SKILL.md")], resolvePath: _ => null));
    }

    [Fact]
    public async Task InstructionsRequireCurrentAuthorityAndNoImplicitInvocationWhenDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root")], [],
            (_, _) => Task.FromResult(Inventory(Skill("alpha", "/a/SKILL.md", implicitAllowed: false),
                Skill("beta", "/b/SKILL.md"))), ct);
        Assert.Null(snapshot.FindImplicitDocument(new("host", "/a/SKILL.md")));
        Assert.Equal("beta", snapshot.FindImplicitDocument(new("host", "/b/SKILL.md"))!.Document.Name);
        var selected = snapshot.Select([new CodexTextInput("$alpha $beta")]);
        var instructions = await catalog.ReadInstructionsAsync(Context, snapshot, selected, (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Context, request.Context);
            if (request.Skill.Document.Name == "beta") throw new UnauthorizedAccessException();
            return Task.FromResult("Exact skill contents");
        }, ct);
        Assert.Equal("Exact skill contents", Assert.Single(instructions.Items).Contents);
        Assert.Single(instructions.Errors);
        catalog.Invalidate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ReadInstructionsAsync(Context, snapshot, selected,
            (_, _) => throw new InvalidOperationException("Stale snapshot must not read."), ct));
    }

    [Fact]
    public async Task InFlightDiscoveryAndReadCannotPublishAfterInvalidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = catalog.DiscoverAsync(Context, [Root("/root")], [], async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Inventory(Skill("alpha", "/a/SKILL.md"));
        }, ct);
        await entered.Task.WaitAsync(ct);
        catalog.Invalidate();
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        var snapshot = await catalog.DiscoverAsync(Context, [Root("/root")], [],
            (_, _) => Task.FromResult(Inventory(Skill("alpha", "/a/SKILL.md"))), ct);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = catalog.ReadInstructionsAsync(Context, snapshot, snapshot.Entries, async (_, token) =>
        {
            readEntered.TrySetResult();
            await readRelease.Task.WaitAsync(token);
            return "Stale content";
        }, ct);
        await readEntered.Task.WaitAsync(ct);
        catalog.Invalidate();
        readRelease.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reading);
    }

    [Fact]
    public async Task NewAuthorityFencesPreviousScanBeforeWaitingForItsCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new CodexSkillCatalog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = catalog.DiscoverAsync(Context, [Root("/root")], [], async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Inventory(Skill("old", "/old/SKILL.md"));
        }, ct);
        await entered.Task.WaitAsync(ct);
        var newer = catalog.DiscoverAsync(Context with { AccessKey = "revoked" }, [Root("/root")], [],
            (_, _) => Task.FromResult(Inventory()), ct);
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);
        Assert.Empty((await newer).Entries);
    }

    [Fact]
    public void FrontmatterExtractsOnlyLeadingYamlAndNormalizesDecodedScalars()
    {
        var result = CodexSkillFrontmatter.Parse("---\r\nname: omitted\r\ndescription: |\r\n  long description\r\n---\r\nDo not parse the body.", "fallback", yaml =>
        {
            Assert.Equal("name: omitted\ndescription: |\n  long description", yaml);
            return Json("""{"description":"  long\n description ","metadata":{"short-description":" short  desc "}}""");
        });
        Assert.Equal(new CodexSkillFrontmatterMetadata("fallback", "long description", "short desc"), result);
        Assert.Throws<FormatException>(() => CodexSkillFrontmatter.Parse("No frontmatter", "fallback",
            _ => throw new InvalidOperationException("No decoder call.")));
        Assert.Throws<FormatException>(() => CodexSkillFrontmatter.Parse("---\na\n---", "fallback", _ => Json("{}")));
        Assert.Throws<FormatException>(() => CodexSkillFrontmatter.Parse("---\na\n---", new string('a', 65),
            _ => Json("""{"description":"x"}""")));
    }
}
