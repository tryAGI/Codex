using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class NativeMailSamplingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly JsonElement Mail = Json("""
        {"type":"agent_message","author":"parent/child","recipient":"parent","content":[{"type":"input_text","text":"Message Type: FINAL_ANSWER\nresult"}]}
        """);
    private static readonly JsonElement User = Json("""{"role":"user","content":"continue"}""");
    private static readonly JsonElement Checkpoint = Json("""{"type":"compaction","encrypted_content":"opaque"}""");
    private const string Success = """{"type":"response.completed","response":{"id":"reasoning-response","model":"model","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new CodexCredentials("test", "account"));
    }

    private sealed class Budget : ICodexUsageObserver
    {
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Handler(string response, List<JsonElement> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add(Json(await request.Content!.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + response + "\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static CodexResponseOptions Settings(Guid thread, Guid turn, string instructions = "instructions") =>
        new("model", instructions) { Identity = new("session", thread.ToString(), turn.ToString()) };

    private static CodexThreadAgent Agent(CodexThreadStore store, List<JsonElement> requests, string response = Success) =>
        new(store, new Credentials(), new Budget(), clientFactory: (credentials, usage) =>
            new CodexClient(credentials, handler: new Handler(response, requests), usageObserver: usage));

    private static Task<CodexThreadDocument> Compact(CodexThreadStore store, CodexThreadCompactionCandidate candidate)
        => store.InstallCompactionAsync(candidate, new(candidate.Model, "compact", Checkpoint, null,
            candidate.History, candidate.ExecutedCallIds), cancellationToken: Ct);

    [Fact]
    public async Task SuccessfulNormalInferenceProvesDistinctIdenticalOccurrencesAcrossRestartAndFork()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var requests = new List<JsonElement>();
        var turn = Guid.NewGuid();
        var settings = Settings(thread.Id, turn);
        var completed = await Agent(store, requests).RunAsync(thread.Id, thread.Revision, turn, "runner",
            settings, [Mail, Mail, User], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        var receipt = Assert.Single(completed.Thread.Turns[0].Dispatches);
        Assert.Equal("succeeded", receipt.State);
        var proof = receipt.MailSampling!;
        Assert.Equal("reasoning-response", proof.ResponseId);
        Assert.Equal(2, proof.Occurrences.Length);
        Assert.NotEqual(proof.Occurrences[0].Sequence, proof.Occurrences[1].Sequence);
        Assert.Equal(proof.Occurrences[0].ItemSha256, proof.Occurrences[1].ItemSha256);
        Assert.All(proof.Occurrences, occurrence => Assert.Equal(turn, occurrence.TurnId));
        Assert.All(Assert.Single(requests).GetProperty("input").EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("native_mail_sequence", out _));
            Assert.False(item.TryGetProperty("native_mail_sampled", out _));
        });

        var restarted = new CodexThreadStore(storage, "account");
        var loaded = await restarted.ReadAsync(thread.Id, Ct);
        var candidate = await restarted.CaptureCompactionAsync(loaded.Id, loaded.Revision, settings, Ct);
        Assert.Equal(proof.Occurrences.Select(item => item.Sequence),
            candidate.HistoryMetadata.Take(2).Select(value => CodexNativeMailSampling.Sequence(value)!.Value));
        Assert.All(candidate.HistoryMetadata.Take(2), value =>
            Assert.True(CodexRetainedJournalMetadata.Flag(value, "native_mail_sampled")));
        var installed = await Compact(restarted, candidate);
        Assert.DoesNotContain(installed.CompactionCheckpoint!.Projection, CodexNativeMailSampling.IsMail);
        Assert.Equal(2, installed.Turns[0].Input.Count(CodexNativeMailSampling.IsMail));

        var fork = await restarted.ForkAsync(installed.Id, installed.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.CompactionCheckpoint);
        Assert.Equal(2, CodexNativeMailSampling.Sampled(fork).Count);

        var nextTurn = Guid.NewGuid();
        var next = await Agent(restarted, requests).RunAsync(installed.Id, installed.Revision, nextTurn, "runner",
            Settings(installed.Id, nextTurn), [Mail, User], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        var nextProof = Assert.Single(Assert.Single(next.Thread.Turns[^1].Dispatches).MailSampling!.Occurrences);
        Assert.Equal(nextTurn, nextProof.TurnId);
        Assert.True(nextProof.Sequence > proof.Occurrences[^1].Sequence);
        Assert.Equal(3, CodexNativeMailSampling.Sampled(next.Thread).Count);

        // A proof is bound to an exact occurrence and a matching settled response.
        var forgedReceipt = receipt with { MailSampling = proof with { Occurrences = [proof.Occurrences[0] with
        { ItemSha256 = new string('0', 64) }] } };
        var forged = loaded with { Turns = [loaded.Turns[0] with { Dispatches = [forgedReceipt] }] };
        Assert.Throws<InvalidDataException>(() => restarted.ValidateSnapshot(CodexThreadStore.Encode(forged)));
    }

    [Fact]
    public async Task LaterSampleDoesNotAcknowledgeOlderMailOmittedByCompaction()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var oversizedNode = JsonNode.Parse(Mail.GetRawText())!.AsObject();
        oversizedNode["content"]![0]!["text"] = new string('x', 41000);
        var oversized = Json(oversizedNode.ToJsonString());
        var initial = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, initial, "runner", [oversized, Mail, User], cancellationToken: Ct);
        thread = await store.CompleteAsync(thread.Id, thread.Revision, initial, "runner", "completed", [], Ct);
        var settings = Settings(thread.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        var firstSequence = CodexNativeMailSampling.Sequence(candidate.HistoryMetadata[0])!.Value;
        var secondSequence = CodexNativeMailSampling.Sequence(candidate.HistoryMetadata[1])!.Value;
        thread = await Compact(store, candidate);
        Assert.Empty(CodexNativeMailSampling.Sampled(thread)); // Compaction is not delivery.
        Assert.Single(thread.CompactionCheckpoint!.Projection, CodexNativeMailSampling.IsMail);
        var requests = new List<JsonElement>();
        var next = Guid.NewGuid();
        var completed = await Agent(store, requests).RunAsync(thread.Id, thread.Revision, next, "runner", Settings(thread.Id, next),
            [User], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        var proof = Assert.Single(Assert.Single(completed.Thread.Turns[^1].Dispatches).MailSampling!.Occurrences);
        Assert.Equal(secondSequence, proof.Sequence);
        Assert.DoesNotContain(firstSequence, CodexNativeMailSampling.Sampled(completed.Thread));
        // A scope change reconstructs the original raw history. The omitted mail
        // must remain pending, even though a later occurrence was actually sampled.
        var rebuilt = await store.CaptureCompactionAsync(thread.Id, completed.Thread.Revision,
            Settings(thread.Id, Guid.NewGuid(), "new instructions"), Ct);
        Assert.Equal(firstSequence, CodexNativeMailSampling.Sequence(rebuilt.HistoryMetadata[0]));
        Assert.False(CodexRetainedJournalMetadata.Flag(rebuilt.HistoryMetadata[0], "native_mail_sampled"));
        Assert.True(CodexRetainedJournalMetadata.Flag(rebuilt.HistoryMetadata[1], "native_mail_sampled"));
    }

    [Theory]
    [InlineData("""{"type":"response.incomplete","response":{"id":"incomplete","usage":{"output_tokens":2}}}""")]
    [InlineData("""{"type":"response.completed","response":{"id":"invalid","model":"model","output":null}}""")]
    public async Task IncompleteOrRejectedTerminalNeverProvesSampling(string response)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        await Assert.ThrowsAsync<CodexProtocolException>(() => Agent(store, [], response).RunAsync(
            thread.Id, thread.Revision, turn, "runner", Settings(thread.Id, turn), [Mail, User],
            (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct));
        var loaded = await store.ReadAsync(thread.Id, Ct);
        Assert.Empty(CodexNativeMailSampling.Sampled(loaded));
        Assert.All(loaded.Turns[0].Dispatches, receipt => Assert.Null(receipt.MailSampling));
        var candidate = await store.CaptureCompactionAsync(loaded.Id, loaded.Revision, Settings(loaded.Id, Guid.NewGuid()), Ct);
        var compacted = await Compact(store, candidate);
        Assert.Single(compacted.CompactionCheckpoint!.Projection, CodexNativeMailSampling.IsMail);
    }
}
