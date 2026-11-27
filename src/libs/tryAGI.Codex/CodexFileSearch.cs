using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Authenticated search scope. A root in a request is not authority to inspect it.</summary>
/// <param name="ConnectionId">Host-authenticated connection.</param>
/// <param name="Root">Normalized absolute root to authorize.</param>
public sealed record CodexFileSearchRoot(string ConnectionId, string Root);

/// <summary>One authorized relative path; file contents are never requested by the matcher.</summary>
/// <param name="Path">Path relative to the authorized root.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
public sealed record CodexFileSearchEntry(string Path, bool IsDirectory);

/// <summary>Trusted host search source. The source must authorize root and entry disclosure,
/// enforce safe rooted enumeration and ignore policy, and honor cancellation. A root
/// authorization is not a substitute for preventing traversal outside that root.</summary>
public interface ICodexFileSearchSource
{
    /// <summary>Checks the exact root before acknowledging discovery or a session start.</summary>
    ValueTask AuthorizeAsync(CodexFileSearchRoot root, CancellationToken cancellationToken);

    /// <summary>Enumerates only authorized, non-ignored paths. Revalidate authority before
    /// enumeration; never rely solely on a prior AuthorizeAsync call.</summary>
    IAsyncEnumerable<CodexFileSearchEntry> EnumerateAsync(CodexFileSearchRoot root, CancellationToken cancellationToken);
}

/// <summary>One pinned fuzzy-search wire result. Indices are sorted grapheme positions.</summary>
/// <param name="Root">Authorized search root.</param>
/// <param name="Path">Relative path.</param>
/// <param name="MatchType">File or directory.</param>
/// <param name="FileName">Basename.</param>
/// <param name="Score">Matcher score, with priority for basename matches.</param>
/// <param name="Indices">Matched positions for highlighting.</param>
public sealed record CodexFileSearchMatch(string Root, string Path,
    [property: JsonPropertyName("match_type")] string MatchType,
    [property: JsonPropertyName("file_name")] string FileName, uint Score, uint[] Indices);

/// <summary>Host-side asynchronous search error. Not a successful completion notification.</summary>
/// <param name="SessionId">Session whose query failed.</param>
/// <param name="Query">Query that failed.</param>
/// <param name="Error">Actual source, matching or notification error.</param>
public sealed record CodexFileSearchFailure(string SessionId, string Query, Exception Error)
{
    /// <inheritdoc />
    public override string ToString() => "File search failure (redacted)";
}

/// <summary>Connection-owned native fuzzyFileSearch RPC host. An explicit trusted source is
/// mandatory; no implicit local filesystem or model tool is registered. Stop/update/disconnect
/// drain prior callbacks before acknowledging the lifecycle change. Notification and source
/// callbacks must honor cancellation and must not re-enter this host's lifecycle methods.
/// The matcher is a deterministic native subsequence approximation, not Nucleo score parity.</summary>
public sealed class CodexFileSearch : IAsyncDisposable
{
    private const int Limit = 50;
    private const int MaximumRoots = 16;
    private const int MaximumJobs = 16;
    private const int MaximumQuery = 256;
    private const int MaximumPath = 4096;
    private readonly string connectionId;
    private readonly ICodexFileSearchSource source;
    private readonly Func<string, JsonElement, CancellationToken, ValueTask> notify;
    private readonly Func<CodexFileSearchFailure, CancellationToken, ValueTask> failure;
    private readonly int maximumEntries;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> searches = new(StringComparer.Ordinal);
    private bool disposed;

    private sealed class Job(CancellationTokenSource stop, Task<JsonElement> task, bool session = false)
    {
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task<JsonElement> Task { get; } = task;
        internal bool IsSession { get; } = session;
    }

    private sealed class Session(string[] roots)
    {
        internal string[] Roots { get; } = roots;
        internal string? Query { get; set; }
        internal Job? Job { get; set; }
    }

    /// <summary>Creates a bounded search host with explicit source and transport callbacks.</summary>
    public CodexFileSearch(string connectionId, ICodexFileSearchSource source,
        Func<string, JsonElement, CancellationToken, ValueTask> notify,
        Func<CodexFileSearchFailure, CancellationToken, ValueTask> failure,
        int maximumEntries = 100000)
    {
        ValidateIdentity(connectionId);
        this.connectionId = connectionId;
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        this.failure = failure ?? throw new ArgumentNullException(nameof(failure));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntries, 1000000);
        this.maximumEntries = maximumEntries;
    }

    /// <summary>Returns whether this host implements the pinned file search method.</summary>
    public static bool Handles(string method) => method is "fuzzyFileSearch" or "fuzzyFileSearch/sessionStart"
        or "fuzzyFileSearch/sessionUpdate" or "fuzzyFileSearch/sessionStop";

    /// <summary>Serves one search request. The caller must route notifications to the same
    /// authenticated connection. A canceled one-shot search has no successful result.</summary>
    public async Task<JsonElement> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unknown file search method.");
        if (parameters.ValueKind != JsonValueKind.Object) throw Invalid("Expected file search parameters.");
        CodexCommandExecution.EnsureUniqueFields(parameters);
        if (method == "fuzzyFileSearch")
        {
            var input = CodexCommandExecution.Read(parameters, CodexFileSearchJson.Default.CodexFileSearchParams);
            return await OneShotAsync(Roots(input.Roots, allowEmpty: true), Query(input.Query), input.CancellationToken,
                cancellationToken).ConfigureAwait(false);
        }

        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (method == "fuzzyFileSearch/sessionStart")
            {
                var input = CodexCommandExecution.Read(parameters, CodexFileSearchJson.Default.CodexFileSearchStartParams);
                ValidateIdentity(input.SessionId);
                var roots = Roots(input.Roots, allowEmpty: false);
                if (sessions.ContainsKey(input.SessionId)) throw Invalid("Duplicate active search session.");
                if (sessions.Count >= MaximumJobs) throw Invalid("Search session capacity exhausted.");
                await AuthorizeAsync(roots, cancellationToken).ConfigureAwait(false);
                sessions.Add(input.SessionId, new(roots));
            }
            else if (method == "fuzzyFileSearch/sessionUpdate")
            {
                var input = CodexCommandExecution.Read(parameters, CodexFileSearchJson.Default.CodexFileSearchUpdateParams);
                var query = Query(input.Query);
                var session = GetSession(input.SessionId);
                if (session.Query == query && session.Job is { Task.IsCompletedSuccessfully: true } previous &&
                    previous.Task.Result.ValueKind == JsonValueKind.Object)
                {
                    await AuthorizeAsync(session.Roots, cancellationToken).ConfigureAwait(false);
                    return Empty();
                }
                if (session.Job is { } old)
                {
                    try { await DrainAsync(old).ConfigureAwait(false); }
                    finally { session.Job = null; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Recheck on every query so a previously admitted session cannot keep stale root authority.
                await AuthorizeAsync(session.Roots, cancellationToken).ConfigureAwait(false);
                session.Query = query;
                var stop = new CancellationTokenSource();
                session.Job = new(stop, Task.Run(() => RunSessionAsync(input.SessionId, session.Roots, query, stop.Token)), session: true);
            }
            else
            {
                var input = CodexCommandExecution.Read(parameters, CodexFileSearchJson.Default.CodexFileSearchStopParams);
                var session = GetSession(input.SessionId);
                sessions.Remove(input.SessionId);
                if (session.Job is { } old) await DrainAsync(old).ConfigureAwait(false);
            }
            return Empty();
        }
        finally { lifecycle.Release(); }
    }

    private async Task<JsonElement> OneShotAsync(string[] roots, string query, string? cancellationKey, CancellationToken ct)
    {
        if (cancellationKey is not null) ValidateIdentity(cancellationKey);
        var key = cancellationKey is null ? "internal:" + Guid.NewGuid().ToString("N") : "client:" + cancellationKey;
        Job job;
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (searches.Remove(key, out var old)) await DrainAsync(old).ConfigureAwait(false);
            if (searches.Count >= MaximumJobs) throw Invalid("Search capacity exhausted.");
            ct.ThrowIfCancellationRequested();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(TimeSpan.FromSeconds(30));
            job = new(stop, Task.Run(async () =>
            {
                var matches = await SearchAsync(roots, query, null, stop.Token).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new CodexFileSearchResult(matches), CodexFileSearchJson.Default.CodexFileSearchResult);
            }));
            searches.Add(key, job);
        }
        finally { lifecycle.Release(); }

        try { return await job.Task.ConfigureAwait(false); }
        finally
        {
            await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (searches.TryGetValue(key, out var current) && ReferenceEquals(job, current))
                {
                    searches.Remove(key);
                    job.Stop.Dispose();
                }
            }
            finally { lifecycle.Release(); }
        }
    }

    private async Task<JsonElement> RunSessionAsync(string id, string[] roots, string query, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        try
        {
            var last = Array.Empty<CodexFileSearchMatch>();
            var published = false;
            async ValueTask Publish(CodexFileSearchMatch[] matches)
            {
                ct.ThrowIfCancellationRequested();
                if (published && Same(last, matches)) return;
                await notify("fuzzyFileSearch/sessionUpdated", JsonSerializer.SerializeToElement(
                    new CodexFileSearchSessionUpdated(id, query, matches), CodexFileSearchJson.Default.CodexFileSearchSessionUpdated), ct).ConfigureAwait(false);
                last = matches;
                published = true;
            }
            var final = await SearchAsync(roots, query, Publish, ct).ConfigureAwait(false);
            await Publish(final).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await notify("fuzzyFileSearch/sessionCompleted", JsonSerializer.SerializeToElement(
                new CodexFileSearchSessionCompleted(id), CodexFileSearchJson.Default.CodexFileSearchSessionCompleted), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Never report a successful completion for a failed source or delivery. If the
            // failure callback itself fails, the session lifecycle surfaces that exception.
            stop.ThrowIfCancellationRequested();
            await failure(new(id, query, error), stop).ConfigureAwait(false);
            return default;
        }
        return Empty();
    }

    private async Task<CodexFileSearchMatch[]> SearchAsync(string[] roots, string query,
        Func<CodexFileSearchMatch[], ValueTask>? publish, CancellationToken ct)
    {
        await AuthorizeAsync(roots, ct).ConfigureAwait(false);
        if (query.Length == 0) return [];
        var pattern = Elements(query);
        var ranked = new SortedSet<CodexFileSearchMatch>(MatchComparer.Instance);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var scanned = 0;
        long retainedPathCharacters = 0;
        foreach (var root in roots)
        {
            await foreach (var entry in source.EnumerateAsync(new(connectionId, root), ct).WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > maximumEntries) throw Invalid("File search scan budget exceeded.");
                var full = ValidateEntry(root, entry);
                if (!seen.Add(full)) continue;
                retainedPathCharacters += full.Length;
                if (retainedPathCharacters > 8 * 1024 * 1024) throw Invalid("File search path memory budget exceeded.");
                var match = Match(root, entry, pattern);
                if (match is not null)
                {
                    ranked.Add(match);
                    if (ranked.Count > Limit) ranked.Remove(ranked.Max!);
                }
                if (publish is not null && scanned % 256 == 0)
                    await publish(ranked.ToArray()).ConfigureAwait(false);
            }
        }
        ct.ThrowIfCancellationRequested();
        return ranked.ToArray();
    }

    private async Task AuthorizeAsync(string[] roots, CancellationToken ct)
    {
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            await source.AuthorizeAsync(new(connectionId, root), ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static string ValidateEntry(string root, CodexFileSearchEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Path) || entry.Path.Length > MaximumPath ||
            entry.Path.Any(char.IsControl) || Path.IsPathRooted(entry.Path) ||
            entry.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("File search source returned an invalid relative path.");
        var full = Path.GetFullPath(Path.Combine(root, entry.Path));
        if (!CodexFileSystem.IsWithin(full, root) || string.Equals(full, root, CodexFileSystem.PathComparison))
            throw new InvalidDataException("File search source returned a path outside its root.");
        return full;
    }

    private static CodexFileSearchMatch? Match(string root, CodexFileSearchEntry entry, string[] query)
    {
        var path = entry.Path;
        var name = Path.GetFileName(path);
        var text = Elements(path);
        var basename = Elements(name);
        var offset = text.Length - basename.Length;
        var indices = Find(basename, query);
        var bonus = indices is not null ? 65536u : 0u;
        if (indices is not null)
        {
            for (var i = 0; i < indices.Length; i++) indices[i] += (uint)offset;
        }
        else indices = Find(text, query);
        if (indices is null) return null;

        // Deterministic approximation: reward consecutive and early matches, then shorter
        // spans. Keep the filename-priority bit separate from the bounded path score.
        var gaps = (int)(indices[^1] - indices[0]) + 1 - indices.Length;
        var leading = (int)indices[0] - (bonus != 0 ? offset : 0);
        var score = (uint)Math.Clamp(query.Length * 64 - gaps * 8 - leading * 2 - Math.Min(text.Length, 256), 1, 65535);
        return new(root, path, entry.IsDirectory ? "directory" : "file", name, bonus + score, indices);
    }

    private static string[] Elements(string value)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        var elements = new string[starts.Length];
        for (var i = 0; i < starts.Length; i++)
            elements[i] = value[starts[i]..(i + 1 < starts.Length ? starts[i + 1] : value.Length)].Normalize(NormalizationForm.FormC);
        return elements;
    }

    private static uint[]? Find(string[] text, string[] query)
    {
        var indices = new uint[query.Length];
        var cursor = 0;
        for (var i = 0; i < text.Length && cursor < query.Length; i++)
            if (string.Equals(text[i], query[cursor], StringComparison.OrdinalIgnoreCase))
                indices[cursor++] = (uint)i;
        return cursor == query.Length ? indices : null;
    }

    private static bool Same(CodexFileSearchMatch[] a, CodexFileSearchMatch[] b) =>
        a.Length == b.Length && a.Zip(b).All(x => x.First.Root == x.Second.Root && x.First.Path == x.Second.Path &&
            x.First.Score == x.Second.Score && x.First.MatchType == x.Second.MatchType &&
            x.First.Indices.SequenceEqual(x.Second.Indices));

    private sealed class MatchComparer : IComparer<CodexFileSearchMatch>
    {
        internal static readonly MatchComparer Instance = new();
        public int Compare(CodexFileSearchMatch? a, CodexFileSearchMatch? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;
            var score = b.Score.CompareTo(a.Score);
            if (score != 0) return score;
            var path = ScalarOrder(a.Path, b.Path);
            return path != 0 ? path : ScalarOrder(a.Root, b.Root);
        }

        private static int ScalarOrder(string left, string right)
        {
            // Rust string ordering is UTF-8 byte ordering, equivalent to scalar order
            // for well-formed Unicode, not .NET UTF-16 code-unit ordering.
            var a = left.EnumerateRunes().GetEnumerator();
            var b = right.EnumerateRunes().GetEnumerator();
            while (a.MoveNext())
            {
                if (!b.MoveNext()) return 1;
                var c = a.Current.Value.CompareTo(b.Current.Value);
                if (c != 0) return c;
            }
            return b.MoveNext() ? -1 : 0;
        }
    }

    private Session GetSession(string id)
    {
        ValidateIdentity(id);
        return sessions.TryGetValue(id, out var session) ? session : throw Invalid("Unknown file search session.");
    }

    private static string[] Roots(string[]? roots, bool allowEmpty)
    {
        if (roots is null || roots.Length > MaximumRoots || (!allowEmpty && roots.Length == 0))
            throw Invalid("Invalid file search roots.");
        var values = new string[roots.Length];
        for (var i = 0; i < roots.Length; i++)
        {
            var path = roots[i];
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPath || path.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(path)) throw Invalid("File search roots must be absolute.");
            values[i] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        return values.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static string Query(string? query)
    {
        if (query is null || query.Length > MaximumQuery || query.Any(char.IsControl)) throw Invalid("Invalid file search query.");
        return query;
    }

    private static void ValidateIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw Invalid("Invalid file search identity.");
    }

    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);
    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new CommandEmpty(), CodexCommandJson.Default.CommandEmpty);

    private static async Task DrainAsync(Job job)
    {
        try
        {
            try { await job.Stop.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await job.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
                catch (Exception) when (!job.IsSession && job.Task.IsFaulted) { /* The originating request observes it. */ }
            }
        }
        finally { job.Stop.Dispose(); }
    }

    /// <summary>Stops admission and drains all session and one-shot callbacks before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            var jobs = sessions.Values.Select(x => x.Job).OfType<Job>().Concat(searches.Values).ToArray();
            sessions.Clear();
            searches.Clear();
            await Task.WhenAll(jobs.Select(DrainAsync)).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
}

internal sealed record CodexFileSearchParams([property: JsonRequired] string Query, [property: JsonRequired] string[] Roots,
    string? CancellationToken = null);
internal sealed record CodexFileSearchStartParams([property: JsonRequired] string SessionId, [property: JsonRequired] string[] Roots);
internal sealed record CodexFileSearchUpdateParams([property: JsonRequired] string SessionId, [property: JsonRequired] string Query);
internal sealed record CodexFileSearchStopParams([property: JsonRequired] string SessionId);
internal sealed record CodexFileSearchResult(CodexFileSearchMatch[] Files);
internal sealed record CodexFileSearchSessionUpdated(string SessionId, string Query, CodexFileSearchMatch[] Files);
internal sealed record CodexFileSearchSessionCompleted(string SessionId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexFileSearchParams))]
[JsonSerializable(typeof(CodexFileSearchStartParams))]
[JsonSerializable(typeof(CodexFileSearchUpdateParams))]
[JsonSerializable(typeof(CodexFileSearchStopParams))]
[JsonSerializable(typeof(CodexFileSearchResult))]
[JsonSerializable(typeof(CodexFileSearchSessionUpdated))]
[JsonSerializable(typeof(CodexFileSearchSessionCompleted))]
internal sealed partial class CodexFileSearchJson : JsonSerializerContext;
