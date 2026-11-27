using System.Diagnostics;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexSqliteApprovalStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual SQLite adapter requires macOS.");
    private static CodexApprovalAction Action(CodexApprovalKind kind = CodexApprovalKind.Command) =>
        new(kind, "connection", Guid.NewGuid().ToString("D"),
            CodexApprovalAction.Fingerprint("exact resolved action"u8), "thread", "turn", "item");
    private static CodexApprovalStore Store(ICodexApprovalStorage storage, string account = "account") =>
        new(storage, account, (request, _) => request.ActorId == "reviewer"
            ? ValueTask.CompletedTask : throw new UnauthorizedAccessException());
    private static async Task<CodexApprovalRecord> Approve(CodexApprovalStore store, Guid id, CodexApprovalAction action)
    {
        var pending = await store.RequestAsync(id, action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        return await store.DecideAsync(id, action, pending.Revision, true, "reviewer", Ct);
    }
    private static CodexApprovalDispatcher Dispatcher(CodexApprovalStore store) =>
        new(store, (_, _) => ValueTask.CompletedTask);

    [Fact]
    public async Task DurableRecordsSurviveIndependentConnectionReopenAndDoNotRecreate()
    {
        RequireMac();
        using var fixture = new Fixture();
        var action = Action();
        var id = Guid.NewGuid();
        CodexApprovalRecord approved;
        await using (var first = await fixture.Open(create: true))
        {
            approved = await Approve(Store(first), id, action);
            var sameId = await Store(first, "different-account").RequestAsync(id, Action(),
                DateTimeOffset.UtcNow.AddMinutes(5), Ct);
            Assert.Equal(CodexApprovalState.Pending, sameId.State);
        }

        await using (var reopened = await fixture.Open())
        {
            var store = Store(reopened);
            Assert.Equal(approved, await store.ReadAsync(id, Ct));
            Assert.Null(await Store(reopened, "missing-account").ReadAsync(id, Ct));
            Assert.Equal(CodexApprovalState.Pending, (await Store(reopened, "different-account").ReadAsync(id, Ct))!.State);
            Assert.NotNull(await store.TryConsumeAsync(id, action, Ct));
        }

        await using var recovered = await fixture.Open();
        var final = Store(recovered);
        Assert.Equal(CodexApprovalState.Consumed, (await final.ReadAsync(id, Ct))!.State);
        Assert.Null(await final.TryConsumeAsync(id, action, Ct));
        Assert.Equal(CodexApprovalState.Consumed,
            (await final.RequestAsync(id, action, approved.ExpiresAt, Ct)).State);
    }

    [Fact]
    public async Task ActualConcurrentConnectionsAdmitExactlyOneDispatch()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var first = await fixture.Open(create: true);
        await using var second = await fixture.Open();
        var id = Guid.NewGuid();
        var action = Action();
        await Approve(Store(first), id, action);
        var count = 0;

        async Task<bool> Attempt(ICodexApprovalStorage storage)
        {
            try
            {
                return await Dispatcher(Store(storage)).DispatchAsync(id, action, (_, _) =>
                {
                    Interlocked.Increment(ref count);
                    return Task.FromResult(true);
                }, Ct);
            }
            catch (UnauthorizedAccessException) { return false; }
        }

        var results = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(results, x => x);
        Assert.Equal(1, count);
        Assert.Equal(CodexApprovalState.Consumed, (await Store(second).ReadAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task ConcurrentRevocationAndConsumptionHaveOneDurableWinner()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var first = await fixture.Open(create: true);
        await using var second = await fixture.Open();
        var store = Store(first);
        var other = Store(second);
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var action = Action();
            var approved = await Approve(store, id, action);
            async Task<bool> Revoke()
            {
                try
                {
                    await other.RevokeAsync(id, action, approved.Revision, "reviewer", Ct);
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }

            var consume = store.TryConsumeAsync(id, action, Ct);
            var revoke = Revoke();
            await Task.WhenAll(consume, revoke);
            var consumed = await consume;
            var revoked = await revoke;
            Assert.NotEqual(consumed is not null, revoked);
            var record = await other.ReadAsync(id, Ct);
            Assert.Equal(revoked ? CodexApprovalState.Revoked : CodexApprovalState.Consumed, record!.State);
            Assert.Equal(approved.Revision + 1, record.Revision);
        }
    }

    [Fact]
    public async Task AmbiguousRealCommitAndFailedEffectCannotAuthorizeReplay()
    {
        RequireMac();
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var action = Action();
        var other = Action(CodexApprovalKind.Tool);
        var count = 0;
        await using (var storage = await fixture.Open(create: true))
        {
            await Approve(Store(storage), id, action);
            await Approve(Store(storage), secondId, other);
            var uncertain = Dispatcher(Store(new AmbiguousStorage(storage)));
            await Assert.ThrowsAsync<IOException>(() => uncertain.DispatchAsync(id, action, (_, _) =>
            {
                count++;
                return Task.FromResult(true);
            }, Ct));
            Assert.Equal(0, count);
            await Assert.ThrowsAsync<IOException>(() => Dispatcher(Store(storage)).DispatchAsync<bool>(secondId, other, (_, _) =>
            {
                count++;
                throw new IOException("Synthetic uncertain effect.");
            }, Ct));
        }

        await using var recovered = await fixture.Open();
        var dispatcher = Dispatcher(Store(recovered));
        foreach (var pair in new[] { (id, action), (secondId, other) })
        {
            Assert.Equal(CodexApprovalState.Consumed, (await Store(recovered).ReadAsync(pair.Item1, Ct))!.State);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.DispatchAsync(pair.Item1, pair.Item2, (_, _) =>
            {
                count++;
                return Task.FromResult(true);
            }, Ct));
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CurrentPolicyAndExactActionAreCheckedBeforeAnyRealDispatch()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var storage = await fixture.Open(create: true);
        var store = Store(storage);
        var id = Guid.NewGuid();
        var action = Action();
        await Approve(store, id, action);
        var count = 0;
        var denied = new CodexApprovalDispatcher(store, (_, _) => throw new UnauthorizedAccessException("Policy revoked."));
        Task<bool> Effect(CodexApprovalRecord _, CancellationToken __)
        {
            count++;
            return Task.FromResult(true);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.DispatchAsync(id, action, Effect, Ct));
        Assert.Equal(CodexApprovalState.Approved, (await store.ReadAsync(id, Ct))!.State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Dispatcher(store).DispatchAsync(id,
            action with { ActionSha256 = CodexApprovalAction.Fingerprint("other action"u8) }, Effect, Ct));
        Assert.Equal(0, count);
        Assert.True(await Dispatcher(store).DispatchAsync(id, action, Effect, Ct));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DurableApprovalGatesActualSeatbeltLeafCommand()
    {
        RequireMac();
        using var fixture = new Fixture();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var path = Path.Combine(workspace, "marker");
        var execution = Guid.NewGuid();
        var request = new CodexCommandRequest(execution, null, "connection",
            Array.AsReadOnly(new[] { "/usr/bin/touch", path }), workspace,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>()),
            false, false, false, null, null, CodexMacOsLeafCommandAdapter.WorkspaceProfile, 4096, TimeSpan.FromSeconds(10));
        // The host resolves the exact request and effective immutable sandbox policy.
        var policy = new CodexMacOsLeafPolicy(workspace, true);
        var resolved = JsonSerializer.SerializeToUtf8Bytes(new { request, policy });
        var action = new CodexApprovalAction(CodexApprovalKind.Command, request.ConnectionId,
            execution.ToString("D"), CodexApprovalAction.Fingerprint(resolved));
        var id = Guid.NewGuid();
        var actual = new CodexMacOsLeafCommandAdapter((input, _) =>
        {
            Assert.Equal(request.ExecutionId, input.ExecutionId);
            Assert.Equal(request.Command.ToArray(), input.Command.ToArray());
            Assert.Equal(request.Cwd, input.Cwd);
            return ValueTask.FromResult(policy);
        });
        // This closure is trusted host code bound to the exact request and policy above.
        async Task<int> Execute(CodexApprovalRecord consumed, CancellationToken ct)
        {
            Assert.Equal(action, consumed.Action);
            Assert.True(DateTimeOffset.UtcNow < consumed.ExpiresAt);
            await using var session = await actual.StartAsync(request, (_, _, _) => ValueTask.CompletedTask, ct);
            return await session.Completion.WaitAsync(ct);
        }
        await using (var storage = await fixture.Open(create: true))
        {
            var store = Store(storage);
            await store.RequestAsync(id, action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Dispatcher(store).DispatchAsync(id, action, Execute, Ct));
            Assert.False(File.Exists(path));
            await store.DecideAsync(id, action, 1, true, "reviewer", Ct);
        }
        await using (var storage = await fixture.Open())
        {
            Assert.Equal(0, await Dispatcher(Store(storage)).DispatchAsync(id, action, Execute, Ct));
            Assert.True(File.Exists(path));
            File.Delete(path);
        }
        await using var recovered = await fixture.Open();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Dispatcher(Store(recovered)).DispatchAsync(id, action, Execute, Ct));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RpcBindingRoundTripsAcrossDatabaseConnections()
    {
        RequireMac();
        using var fixture = new Fixture();
        const string method = "item/commandExecution/requestApproval";
        var action = Action();
        var parameters = JsonSerializer.SerializeToElement(new
        {
            threadId = "thread", turnId = "turn", itemId = "item", startedAtMs = 1,
            availableDecisions = new[] { "accept", "decline" },
        });
        var request = JsonSerializer.SerializeToElement(123);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        Guid id;
        await using (var storage = await fixture.Open(create: true))
        {
            var bridge = new CodexApprovalRpcBridge(Store(storage), "connection");
            var admitted = await bridge.AdmitAsync(request, method, parameters, action, expires, Ct);
            Assert.True(admitted.Created);
            id = admitted.Record.Id;
        }
        await using var reopened = await fixture.Open();
        var store = Store(reopened);
        var next = new CodexApprovalRpcBridge(store, "connection");
        var duplicate = await next.AdmitAsync(request, method, parameters, action, expires, Ct);
        Assert.False(duplicate.Created);
        Assert.Equal(id, duplicate.Record.Id);
        Assert.True(duplicate.Record.RpcBinding!.RequestId.IsNumeric);
        var response = await next.RespondAsync(request, method, action, 1, "reviewer",
            JsonSerializer.SerializeToElement(new { decision = "accept" }), Ct);
        Assert.Equal(CodexApprovalState.Approved, response.Record.State);
    }

    [Fact]
    public async Task AbruptExitDuringActualSQLiteTransactionDoesNotPublishUncommittedRevision()
    {
        RequireMac();
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        var action = Action();
        CodexApprovalRecord pending;
        await using (var storage = await fixture.Open(create: true))
            pending = await Store(storage).RequestAsync(id, action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);

        // The system SQLite shell is a fixture-only separate process. No real approval
        // data is accessed; the transaction changes only this disposable database.
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/sqlite3")
        {
            ArgumentList = { "-batch", "-bail", fixture.Path },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("SQLite fixture process failed to start.");
        try
        {
            await process.StandardInput.WriteLineAsync("BEGIN IMMEDIATE; UPDATE codex_approval SET revision=99;".AsMemory(), Ct);
            await process.StandardInput.WriteLineAsync(".print transaction-ready".AsMemory(), Ct);
            await process.StandardInput.FlushAsync(Ct);
            Assert.Equal("transaction-ready", await process.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct));
            // No COMMIT or stdin EOF is sent.
            process.Kill();
            await process.WaitForExitAsync(Ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
        }

        await using var reopened = await fixture.Open();
        Assert.Equal(pending, await Store(reopened).ReadAsync(id, Ct));
        Assert.Equal(CodexApprovalState.Approved,
            (await Store(reopened).DecideAsync(id, action, 1, true, "reviewer", Ct)).State);
    }

    [Fact]
    public async Task MissingAndForeignDatabasesDoNotSilentlyInitialize()
    {
        RequireMac();
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
        await File.WriteAllBytesAsync(fixture.Path, [], Ct);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(fixture.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open());
        await Assert.ThrowsAsync<IOException>(() => fixture.Open(create: true));
    }

    private sealed class AmbiguousStorage(ICodexApprovalStorage inner) : ICodexApprovalStorage
    {
        public Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken) =>
            inner.ReadAsync(accountId, requestId, cancellationToken);
        public async Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
            CodexApprovalRecord record, CancellationToken cancellationToken)
        {
            var result = await inner.CompareExchangeAsync(accountId, requestId, expectedRevision, record, cancellationToken);
            if (result && record.State == CodexApprovalState.Consumed) throw new IOException("Lost real commit receipt.");
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-approval-sqlite-").FullName);
        internal string Path => System.IO.Path.Combine(Root, "approvals.sqlite");
        internal Task<CodexMacOsSqliteApprovalStorage> Open(bool create = false) =>
            CodexMacOsSqliteApprovalStorage.OpenAsync(Path, create, Ct);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
