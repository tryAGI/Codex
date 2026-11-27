using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

// Rust ModelClientSession owns one immutable sticky-routing token per turn and auth owner.
// Keep one bounded active scope per native client. Late headers from retired scopes cannot
// repopulate the cache, including when a client is reused across accounts or turns.
internal sealed class CodexResponsesRouting
{
    private const string Header = "x-codex-turn-state";
    private readonly object gate = new();
    private Scope? active;
    private object generation = new();
    private string? state;

    private sealed record Scope(CodexResponsesIdentity Identity, Uri Endpoint, string Account,
        string CredentialFingerprint);

    internal object Prepare(HttpRequestMessage request, CodexResponsesIdentity? identity)
    {
        var account = request.Headers.TryGetValues("ChatGPT-Account-Id", out var accounts)
            ? accounts.SingleOrDefault() : null;
        var bearer = request.Headers.Authorization?.Parameter;
        var next = identity is not null && request.RequestUri is { } endpoint &&
            account is not null && bearer is not null
            ? new Scope(identity, endpoint, account,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))))
            : null;
        lock (gate)
        {
            if (next is null || next != active)
            {
                active = next;
                generation = new object();
                state = null;
            }
            if (state is not null) request.Headers.Add(Header, state);
            return generation;
        }
    }

    internal void Capture(HttpResponseMessage response, object requestGeneration)
    {
        if (response.Headers.TryGetValues(Header, out var values)) Capture(values, requestGeneration);
    }

    internal string? GetState(object requestGeneration)
    {
        lock (gate) return ReferenceEquals(generation, requestGeneration) ? state : null;
    }

    internal void Capture(IEnumerable<string> values, object requestGeneration)
    {
        var headers = values.Take(2).ToArray();
        if (headers.Length != 1 || !Valid(headers[0])) return;
        lock (gate)
        {
            if (active is not null && ReferenceEquals(generation, requestGeneration))
                state ??= headers[0];
        }
    }

    private static bool Valid(string value) =>
        value.Length is > 0 and <= 8192 && value.All(c => c is >= '!' and <= '~' && c != ',');
}
