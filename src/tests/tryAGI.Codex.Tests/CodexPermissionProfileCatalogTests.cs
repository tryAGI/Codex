using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexPermissionProfileCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly string Root = Path.GetFullPath(Path.GetTempPath());
    private static string Project(string name) => Path.Combine(Root, name);

    private static CodexThreadConfigurationSnapshot Snapshot(string config, string revision = "r1") =>
        new(revision, [new(CodexConfigurationLayerKind.User, "user", Json(config))],
            Json("""{"permissions":{"managed":true}}"""));

    [Fact]
    public async Task BuiltInsAndConfiguredProfilesHavePinnedOrderingPaginationAndDescriptions()
    {
        var loads = 0;
        var candidateIds = new List<string>();
        var project = Project("codex-project");
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (context, _) =>
        {
            Assert.Equal("owner", context.ConnectionId);
            Assert.Equal(project, context.WorkingDirectory);
            return ValueTask.CompletedTask;
        }, (context, _) =>
        {
            loads++;
            Assert.Equal("permissionProfile/list", context.Method);
            Assert.Null(context.ThreadId);
            Assert.Equal(project, context.WorkingDirectory);
            return Task.FromResult(new CodexThreadConfigurationSnapshot("r1",
                [
                    new(CodexConfigurationLayerKind.User, "user", Json("""
                        {"permissions":{"z":{"description":"Z"},"base":{"description":"Parent","extends":":workspace","filesystem":{":project_roots":"read"}}}}
                        """)),
                    new(CodexConfigurationLayerKind.Project, "project", Json("""
                        {"permissions":{"child":{"extends":"base","network":{"enabled":false}},"z":{"description":"Project Z"}}}
                        """))
                ], Json("""{"permissions":{"managed":true}}""")));
        }, (candidate, _) =>
        {
            candidateIds.Add(candidate.Id);
            Assert.Equal("r1", candidate.ConfigurationRevision);
            Assert.True(candidate.Requirements!.Value.GetProperty("permissions").GetProperty("managed").GetBoolean());
            return ValueTask.FromResult(candidate.Id != CodexPermissionProfileCatalog.DangerFullAccessId);
        });

        var first = await catalog.ListAsync(new(Limit: 3, Cwd: project), Ct);
        Assert.Equal([":read-only", ":workspace", ":danger-full-access"], first.Data.Select(x => x.Id));
        Assert.Equal([true, true, false], first.Data.Select(x => x.Allowed));
        Assert.Equal("3", first.NextCursor);

        var second = await catalog.ListAsync(new(first.NextCursor, 3, project), Ct);
        Assert.Equal(["base", "child", "z"], second.Data.Select(x => x.Id));
        Assert.Equal("Parent", second.Data[0].Description);
        Assert.Null(second.Data[1].Description);
        Assert.Equal("Project Z", second.Data[2].Description);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, loads);
        Assert.Equal([":read-only", ":workspace", ":danger-full-access", "base", "child", "z"], candidateIds);

        var resolved = await catalog.ResolveAsync("child", project, Ct);
        Assert.Equal("base", resolved.Extends);
        Assert.Equal([":workspace", "base", "child"], resolved.Layers.Select(x => x.Id));
        Assert.Null(resolved.Layers[0].Config);
        Assert.Equal("read", resolved.Layers[1].Config!.Value.GetProperty("filesystem").GetProperty(":project_roots").GetString());
        Assert.False(resolved.Layers[2].Config!.Value.GetProperty("network").GetProperty("enabled").GetBoolean());
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task UnauthorizedOrRelativeDirectoryCannotTriggerConfigurationRead()
    {
        var reads = 0;
        var catalog = new CodexPermissionProfileCatalog("owner", Root,
            (_, _) => throw new UnauthorizedAccessException(),
            (_, _) => { reads++; return Task.FromResult(Snapshot("{}")); },
            (_, _) => throw new InvalidOperationException("Must not evaluate."));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.ListAsync(new(Cwd: Project("private")), Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ListAsync(new(Cwd: "relative"), Ct));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task SelectionRevalidatesWorkingDirectoryAndCurrentRevision()
    {
        var allowed = true;
        var revision = "r1";
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(Snapshot("""{"permissions":{"scoped":{"filesystem":{":project_roots":"write"}}}}""", revision)),
            (candidate, _) => ValueTask.FromResult(allowed && candidate.Context.WorkingDirectory == Project("allowed") &&
                candidate.Id != CodexPermissionProfileCatalog.DangerFullAccessId));

        var inside = await catalog.ListAsync(new(Cwd: Project("allowed")), Ct);
        Assert.True(inside.Data.Single(x => x.Id == "scoped").Allowed);
        var outside = await catalog.ListAsync(new(Cwd: Project("outside")), Ct);
        Assert.False(outside.Data.Single(x => x.Id == "scoped").Allowed);
        var resolved = await catalog.ResolveAsync("scoped", Project("allowed"), Ct);
        Assert.Equal("r1", resolved.ConfigurationRevision);

        revision = "r2";
        allowed = false;
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ResolveAsync("scoped", Project("allowed"), Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ResolveAsync("unknown", Project("allowed"), Ct));
        var denied = await catalog.ListAsync(new(Cwd: Project("allowed")), Ct);
        Assert.All(denied.Data, item => Assert.False(item.Allowed));
    }

    [Fact]
    public async Task MalformedAndCyclicProfilesNeverReachAuthorityEvaluator()
    {
        var evaluated = new List<string>();
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(Snapshot("""
                {"permissions":{
                    "a":{"extends":"b"},"b":{"extends":"a"},"missing":{"extends":"unavailable"},
                    "unknown_builtin":{"extends":":unknown"},"invalid":{"extends":42},"valid":{}
                }}
                """)),
            (candidate, _) => { evaluated.Add(candidate.Id); return ValueTask.FromResult(true); });
        var result = await catalog.ListAsync(new(), Ct);
        Assert.Equal([":read-only", ":workspace", ":danger-full-access", "valid"], evaluated);
        Assert.All(result.Data.Where(x => x.Id is "a" or "b" or "missing" or "unknown_builtin" or "invalid"),
            item => Assert.False(item.Allowed));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ResolveAsync("a", cancellationToken: Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ResolveAsync("missing", cancellationToken: Ct));
        Assert.Equal(4, evaluated.Count);
    }

    [Fact]
    public async Task UntrustedProjectLayerCannotInjectOrBroadenProfiles()
    {
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("r1", [
                new(CodexConfigurationLayerKind.User, "user", Json("""{"permissions":{"base":{"network":{"enabled":false}}}}""")),
                new(CodexConfigurationLayerKind.Project, "project",
                    Json("""{"permissions":{"base":{"network":{"enabled":true}},"injected":{}}}"""), Trusted: false)
            ])),
            (candidate, _) =>
            {
                if (candidate.Id == "base")
                    Assert.False(candidate.Layers.Single().Config!.Value.GetProperty("network").GetProperty("enabled").GetBoolean());
                return ValueTask.FromResult(candidate.Id == "base");
            });
        var list = await catalog.ListAsync(new(), Ct);
        Assert.DoesNotContain(list.Data, x => x.Id == "injected");
        Assert.True(list.Data.Single(x => x.Id == "base").Allowed);
    }

    [Fact]
    public async Task RawProfileConstraintsReachHostButAreNotExposedInWireSummary()
    {
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(Snapshot("""{"permissions":{"custom":{"future_constraint":{"secret":"private-value"}}}}""")),
            (candidate, _) =>
            {
                if (candidate.Id == "custom")
                {
                    Assert.Equal("private-value", candidate.Layers.Single().Config!.Value
                        .GetProperty("future_constraint").GetProperty("secret").GetString());
                    Assert.DoesNotContain("private-value", candidate.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain("private-value", candidate.Layers.Single().ToString(), StringComparison.Ordinal);
                }
                return ValueTask.FromResult(false);
            });
        var result = await catalog.HandleAsync("permissionProfile/list", Json("{}"), Ct);
        Assert.DoesNotContain("private-value", result.GetRawText(), StringComparison.Ordinal);
        var custom = result.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "custom");
        Assert.Equal(3, custom.EnumerateObject().Count());
        Assert.False(custom.GetProperty("allowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("""{"cursor":"-1"}""")]
    [InlineData("""{"cursor":"01"}""")]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":513}""")]
    [InlineData("""{"limit":1,"limit":2}""")]
    [InlineData("""{"grant":true}""")]
    public async Task InvalidWireRequestsDoNotReadConfiguration(string input)
    {
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.HandleAsync("permissionProfile/list", Json(input), Ct));
    }

    [Fact]
    public async Task BuiltInShadowingCannotReachEvaluator()
    {
        var catalog = new CodexPermissionProfileCatalog("owner", Root, (_, _) => ValueTask.CompletedTask,
            (_, _) => Task.FromResult(Snapshot("""{"permissions":{":read-only":{"network":{"enabled":true}}}}""")),
            (_, _) => throw new InvalidOperationException("Must not evaluate."));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => catalog.ListAsync(new(), Ct));
    }
}
