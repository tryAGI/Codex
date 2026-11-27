using System.Text.RegularExpressions;

namespace tryAGI.Codex;

/// <summary>Persisted hook preferences. A null field leaves the preceding layer's value intact.</summary>
public sealed record CodexHookPreference(bool? Enabled = null, string? TrustedHash = null,
    string? TrustedContentHash = null);

/// <summary>One host-resolved configuration layer, supplied low to high in precedence.
/// Only user and sessionFlags layers can change hook preferences.</summary>
public sealed record CodexHookPreferenceLayer(string Source, IReadOnlyDictionary<string, CodexHookPreference> Preferences);

/// <summary>Host-discovered hook declaration and current content identities. The host owns
/// canonical key generation, source provenance and fingerprints; model input is not authority.</summary>
public sealed record CodexHookCandidate(string Key, CodexHookDefinition Definition, string DeclarationHash,
    string? ContentHash = null, string? Matcher = null, bool IsManaged = false);

/// <summary>Resolved inventory item including disabled, untrusted and modified declarations.</summary>
public sealed record CodexHookConfigurationEntry(string Key, CodexHookDefinition Definition, bool Enabled,
    string CurrentHash, string? Matcher, string? Error);

/// <summary>Immutable resolved hook inventory. Selection uses current trusted configuration,
/// environment and event/matcher aliases. Actual execution still requires independent admission.</summary>
public sealed class CodexHookConfiguration
{
    private static readonly HashSet<string> MatchedEvents = new(StringComparer.Ordinal)
    {
        "PreToolUse", "PermissionRequest", "PostToolUse", "SessionStart", "SessionEnd",
        "SubagentStart", "SubagentStop", "PreCompact", "PostCompact",
    };
    private static readonly HashSet<string> UnmatchedEvents = new(StringComparer.Ordinal)
    {
        "PreModelRequest", "PostModelRequest", "UserPromptSubmit", "Stop", "Interrupt",
    };

    private readonly Resolved[] resolved;
    private CodexHookConfiguration(Resolved[] items)
    {
        resolved = items;
        Entries = Array.AsReadOnly(items.Select(item => item.Entry).ToArray());
    }

    /// <summary>All resolved declarations, including those ineligible for dispatch.</summary>
    public IReadOnlyList<CodexHookConfigurationEntry> Entries { get; }

    /// <summary>Resolves host-discovered declarations against effective user preferences.
    /// Project, managed and plugin layers cannot write user approval state. Plugin approval
    /// must match both declaration and content hash; legacy disk migration is not implicit.</summary>
    public static CodexHookConfiguration Resolve(IEnumerable<CodexHookCandidate> candidates,
        IEnumerable<CodexHookPreferenceLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(layers);
        var effective = new Dictionary<string, CodexHookPreference>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            if (layer.Source is not ("user" or "sessionFlags")) continue;
            foreach (var (rawKey, state) in layer.Preferences)
            {
                var key = rawKey.Trim();
                if (key.Length == 0 || state is null) continue;
                var previous = effective.GetValueOrDefault(key) ?? new();
                effective[key] = new(state.Enabled ?? previous.Enabled,
                    state.TrustedHash ?? previous.TrustedHash,
                    state.TrustedContentHash ?? previous.TrustedContentHash);
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<Resolved>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.Definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.DeclarationHash);
            if (candidate.ContentHash is not null) ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ContentHash);
            if (!keys.Add(candidate.Key)) throw new ArgumentException("Duplicate hook configuration key.", nameof(candidates));
            var definition = candidate.Definition;
            var state = effective.GetValueOrDefault(candidate.Key);
            var enabled = definition.Builtin || candidate.IsManaged || state?.Enabled != false;
            var trust = definition.Builtin ? "trusted" : candidate.IsManaged ? "managed" :
                state?.TrustedHash is null ? "untrusted" :
                state.TrustedHash == candidate.DeclarationHash &&
                (candidate.ContentHash is null || candidate.ContentHash == state.TrustedContentHash)
                    ? "trusted" : "modified";
            var current = candidate.ContentHash is null ? candidate.DeclarationHash :
                candidate.DeclarationHash + "/" + candidate.ContentHash;
            string? error = null;
            Matcher? matcher = null;
            var pattern = MatchedEvents.Contains(definition.EventName) ? candidate.Matcher : null;
            if (!MatchedEvents.Contains(definition.EventName) && !UnmatchedEvents.Contains(definition.EventName))
                error = "Unknown hook event.";
            else
            {
                try { matcher = new Matcher(pattern); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                { error = "Hook matcher is invalid or unsupported by the bounded regex engine."; }
            }
            items.Add(new(new(candidate.Key, definition with { TrustStatus = trust }, enabled, current, pattern, error), matcher));
        }
        return new(items.OrderBy(item => item.Entry.Definition.DisplayOrder).ToArray());
    }

    /// <summary>Returns each eligible declaration at most once, preserving display order.
    /// Matcher-less events ignore configured patterns, as in Rust. Optional matcher inputs
    /// include host-resolved compatibility aliases, never arbitrary model-selected authority.</summary>
    public IReadOnlyList<CodexHookDefinition> Select(CodexHookContext context,
        IReadOnlyList<string>? matcherInputs = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsInternalSubagent) return [];
        var inputs = (matcherInputs ?? []).Concat(context.ToolName is { } name ? [name] : []).ToArray();
        var results = new List<CodexHookDefinition>();
        foreach (var item in resolved)
        {
            var entry = item.Entry;
            var definition = entry.Definition;
            if (!entry.Enabled || entry.Error is not null || definition.TrustStatus is not ("trusted" or "managed") ||
                definition.EventName != context.EventName ||
                definition.EnvironmentId is not null && definition.EnvironmentId != context.EnvironmentId) continue;
            if (item.Matcher is null) continue;
            // A timeout is a rejected matcher, never permission to broaden its selection.
            try
            {
                if (item.Matcher.Matches(inputs)) results.Add(definition);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new CodexProtocolException("Hook matcher evaluation timed out.");
            }
        }
        return results.AsReadOnly();
    }

    private sealed record Resolved(CodexHookConfigurationEntry Entry, Matcher? Matcher);

    private sealed class Matcher
    {
        private readonly bool all;
        private readonly string[]? exact;
        private readonly Regex? regex;

        internal Matcher(string? pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*")
            {
                all = true;
                return;
            }
            if (pattern.Length > 4096) throw new ArgumentException("Hook matcher is too long.");
            if (pattern.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '|'))
                exact = pattern.Split('|');
            else
                // Use a bounded non-backtracking engine, with no fallback to arbitrary regex execution.
                regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(100));
        }

        internal bool Matches(string[] inputs) => all ||
            (exact is { } candidates ? inputs.Any(input => candidates.Contains(input, StringComparer.Ordinal)) :
                inputs.Any(input => regex!.IsMatch(input)));
    }
}
