using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

// Rust ModelClientSession owns one immutable routing token per turn and auth owner.
// Different backing threads can run concurrently on a native client. Keep at most one
// active scope per logical thread, bounded by an LRU; a retired scope cannot capture headers.
internal sealed class CodexResponsesRouting
{
    private const string Header = "x-codex-turn-state";
    private const int MaximumThreads = 64;
    private readonly object gate = new();
    private readonly Dictionary<ThreadKey, Entry> entries = [];
    private readonly LinkedList<ThreadKey> order = [];
    private Owner? activeOwner;

    private sealed record Owner(Uri Endpoint, string Account, string CredentialFingerprint);
    private sealed record ThreadKey(string SessionId, string ThreadId);
    private sealed class Entry(CodexResponsesIdentity identity, LinkedListNode<ThreadKey> node)
    {
        internal CodexResponsesIdentity Identity { get; } = identity;
        internal LinkedListNode<ThreadKey> Node { get; } = node;
        internal string? State { get; set; }
    }

    internal object Prepare(HttpRequestMessage request, CodexResponsesIdentity? identity)
    {
        var account = request.Headers.TryGetValues("ChatGPT-Account-Id", out var accounts)
            ? accounts.SingleOrDefault() : null;
        var bearer = request.Headers.Authorization?.Parameter;
        var owner = identity is not null && request.RequestUri is { } endpoint &&
            account is not null && bearer is not null
            ? new Owner(endpoint, account,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))))
            : null;
        lock (gate)
        {
            if (owner is null || owner != activeOwner)
            {
                entries.Clear();
                order.Clear();
                activeOwner = owner;
            }
            if (owner is null || identity is null) return new object();
            var key = new ThreadKey(identity.SessionId, identity.ThreadId);
            if (entries.TryGetValue(key, out var entry))
            {
                order.Remove(entry.Node);
                if (entry.Identity != identity) entry = null;
            }
            if (entry is null)
            {
                entry = new Entry(identity, order.AddLast(key));
                entries[key] = entry;
            }
            else order.AddLast(entry.Node);

            if (entries.Count > MaximumThreads)
            {
                var oldest = order.First!;
                entries.Remove(oldest.Value);
                order.RemoveFirst();
            }

            if (entry.State is not null) request.Headers.Add(Header, entry.State);
            return entry;
        }
    }

    internal void Capture(HttpResponseMessage response, object requestGeneration)
    {
        if (response.Headers.TryGetValues(Header, out var values)) Capture(values, requestGeneration);
    }

    // Rust captures sticky state from response.metadata, independently of handshake headers.
    internal void CaptureEvent(JsonElement payload, string type, object requestGeneration)
    {
        if (type != "response.metadata" || !payload.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Object) return;
        var found = headers.EnumerateObject()
            .Where(property => property.Name.Equals(Header, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (found.Length != 1) return;
        var value = found[0].Value;
        while (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1)
            value = value[0];
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } state)
            Capture([state], requestGeneration);
    }

    internal string? GetState(object requestGeneration)
    {
        lock (gate) return Current(requestGeneration)?.State;
    }

    internal void Capture(IEnumerable<string> values, object requestGeneration)
    {
        var headers = values.Take(2).ToArray();
        if (headers.Length != 1 || !Valid(headers[0])) return;
        lock (gate)
        {
            if (Current(requestGeneration) is { } entry) entry.State ??= headers[0];
        }
    }

    private Entry? Current(object generation) => generation is Entry candidate &&
        entries.TryGetValue(candidate.Node.Value, out var active) && ReferenceEquals(active, candidate) ? candidate : null;

    private static bool Valid(string value) =>
        value.Length is > 0 and <= 8192 && value.All(c => c is >= '!' and <= '~' && c != ',');
}
