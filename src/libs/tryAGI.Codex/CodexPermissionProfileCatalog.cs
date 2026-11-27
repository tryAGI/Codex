using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Authenticated, normalized project context for permission configuration discovery.</summary>
/// <param name="ConnectionId">Connection identity supplied by the host.</param>
/// <param name="WorkingDirectory">Absolute project working directory.</param>
public sealed record CodexPermissionProfileContext(string ConnectionId, string WorkingDirectory);

/// <summary>One declaration in a profile inheritance chain, from parent to child.
/// A null configuration identifies a built-in profile, not an unrestricted policy.</summary>
/// <param name="Id">Profile identifier.</param>
/// <param name="Config">Complete raw profile declaration, including unknown constraints.</param>
public sealed record CodexPermissionProfileLayer(string Id, JsonElement? Config)
{
    /// <inheritdoc />
    public override string ToString() => "CodexPermissionProfileLayer [redacted]";
}

/// <summary>Host-only profile selection candidate. It is not an execution authorization or an
/// OS-compiled sandbox. The host must resolve symbolic roots and all policy constraints and reject
/// any unsupported semantics. Raw configuration may contain sensitive data.</summary>
/// <param name="Context">Authenticated project context.</param>
/// <param name="ConfigurationRevision">Revision of the layered configuration and requirements.</param>
/// <param name="Id">Selected profile identifier.</param>
/// <param name="Description">Description from the selected declaration, not its parents.</param>
/// <param name="Extends">Direct parent identifier, if any.</param>
/// <param name="Layers">Complete parent-first declaration chain.</param>
/// <param name="Requirements">Host-resolved managed requirements, if present.</param>
public sealed record CodexPermissionProfileCandidate(CodexPermissionProfileContext Context,
    string ConfigurationRevision, string Id, string? Description, string? Extends,
    IReadOnlyList<CodexPermissionProfileLayer> Layers, JsonElement? Requirements)
{
    /// <inheritdoc />
    public override string ToString() => "CodexPermissionProfileCandidate [redacted]";
}

/// <summary>Wire summary for permissionProfile/list. Allowed means selectable under the
/// effective requirements, not consent to execute any specific command.</summary>
/// <param name="Id">Profile identifier.</param>
/// <param name="Description">Optional display description.</param>
/// <param name="Allowed">Whether selection is allowed by the trusted host.</param>
public sealed record CodexPermissionProfileSummary(string Id, string? Description, bool Allowed);

/// <summary>Permission profile list request.</summary>
/// <param name="Cursor">Opaque cursor from the preceding response.</param>
/// <param name="Limit">Optional page size.</param>
/// <param name="Cwd">Optional absolute working directory.</param>
public sealed record CodexPermissionProfileListParams(string? Cursor = null, int? Limit = null, string? Cwd = null);

/// <summary>Permission profile list response.</summary>
/// <param name="Data">Ordered page of profile summaries.</param>
/// <param name="NextCursor">Cursor for the next page, or null.</param>
public sealed record CodexPermissionProfileListResponse(CodexPermissionProfileSummary[] Data, string? NextCursor);

/// <summary>Connection-bound permission profile discovery and explicit selection. Reuses the
/// native configuration layer resolver without treating the resulting config as authority.
/// The host must authorize the project before config I/O, and evaluate every profile against
/// the effective requirements and supported enforcement. No profile is implicitly allowed.</summary>
public sealed class CodexPermissionProfileCatalog
{
    /// <summary>Built-in read-only profile.</summary>
    public const string ReadOnlyId = ":read-only";
    /// <summary>Built-in workspace profile.</summary>
    public const string WorkspaceId = ":workspace";
    /// <summary>Built-in full-access profile identifier. Discovery does not grant full access.</summary>
    public const string DangerFullAccessId = ":danger-full-access";
    private const int MaximumProfiles = 512;
    private const int MaximumInheritanceDepth = 32;
    private static readonly string[] BuiltIns = [ReadOnlyId, WorkspaceId, DangerFullAccessId];

    private readonly string connectionId;
    private readonly string defaultWorkingDirectory;
    private readonly Func<CodexPermissionProfileContext, CancellationToken, ValueTask> authorizeDirectory;
    private readonly Func<CodexThreadConfigurationContext, CancellationToken, Task<CodexThreadConfigurationSnapshot>> configuration;
    private readonly Func<CodexPermissionProfileCandidate, CancellationToken, ValueTask<bool>> canSelect;

    /// <summary>Creates the catalog with explicit host project authorization, configuration source,
    /// and policy selection evaluator. A denied or unavailable evaluator must never be substituted
    /// with a permissive default. The source must be bound to the same authenticated account.</summary>
    public CodexPermissionProfileCatalog(string connectionId, string defaultWorkingDirectory,
        Func<CodexPermissionProfileContext, CancellationToken, ValueTask> authorizeDirectory,
        Func<CodexThreadConfigurationContext, CancellationToken, Task<CodexThreadConfigurationSnapshot>> configuration,
        Func<CodexPermissionProfileCandidate, CancellationToken, ValueTask<bool>> canSelect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        this.connectionId = connectionId;
        this.defaultWorkingDirectory = Absolute(defaultWorkingDirectory);
        this.authorizeDirectory = authorizeDirectory ?? throw new ArgumentNullException(nameof(authorizeDirectory));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.canSelect = canSelect ?? throw new ArgumentNullException(nameof(canSelect));
    }

    /// <summary>Returns whether this host handles the native permission profile list RPC.</summary>
    public static bool Handles(string method) => method == "permissionProfile/list";

    /// <summary>Serves the pinned permissionProfile/list wire contract.</summary>
    public async Task<JsonElement> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported permission profile method.");
        if (parameters.ValueKind != JsonValueKind.Object) throw Invalid("Expected permission profile parameters.");
        CodexCommandExecution.EnsureUniqueFields(parameters);
        var input = CodexCommandExecution.Read(parameters, CodexPermissionProfileJson.Default.CodexPermissionProfileListParams);
        return JsonSerializer.SerializeToElement(await ListAsync(input, cancellationToken).ConfigureAwait(false),
            CodexPermissionProfileJson.Default.CodexPermissionProfileListResponse);
    }

    /// <summary>Lists the three built-in profiles followed by configured profiles in ordinal order.
    /// Authorization is re-evaluated for the requested directory and every page.</summary>
    public async Task<CodexPermissionProfileListResponse> ListAsync(CodexPermissionProfileListParams input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Limit is < 1 or > MaximumProfiles) throw Invalid("Invalid permission profile page size.");
        var offset = 0;
        if (input.Cursor is { } cursor && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
            cursor != offset.ToString(CultureInfo.InvariantCulture) || offset > MaximumProfiles))
            throw Invalid("Invalid permission profile cursor.");

        var snapshot = await LoadAsync(input.Cwd, cancellationToken).ConfigureAwait(false);
        var ids = BuiltIns.Concat(snapshot.Declarations.Keys.Order(StringComparer.Ordinal)).ToArray();
        if (offset > ids.Length) throw Invalid("Permission profile cursor is out of range.");
        var end = Math.Min(ids.Length, offset + (input.Limit ?? MaximumProfiles));
        var data = new CodexPermissionProfileSummary[end - offset];
        for (var i = offset; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ids[i];
            var candidate = TryCandidate(snapshot, id);
            var allowed = candidate is not null && await canSelect(candidate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            data[i - offset] = new(id, candidate?.Description ?? Description(snapshot, id), allowed);
        }
        return new(data, end < ids.Length ? end.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Reloads the current authorized configuration and resolves an explicitly selected
    /// profile to its full inheritance chain. Unknown, malformed, denied, and cyclic profiles
    /// fail closed. The returned candidate is not a grant and must still be compiled and
    /// enforced for the exact action by the execution adapter.</summary>
    public async Task<CodexPermissionProfileCandidate> ResolveAsync(string id, string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var snapshot = await LoadAsync(cwd, cancellationToken).ConfigureAwait(false);
        var candidate = TryCandidate(snapshot, id);
        if (candidate is null || !await canSelect(candidate, cancellationToken).ConfigureAwait(false))
            throw new CodexThreadRpcException(-32010, "Permission profile is unknown, denied or unsupported.");
        cancellationToken.ThrowIfCancellationRequested();
        return candidate;
    }

    private sealed record Snapshot(CodexPermissionProfileContext Context, string Revision,
        Dictionary<string, JsonElement> Declarations, JsonElement? Requirements);

    private async Task<Snapshot> LoadAsync(string? cwd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = new CodexPermissionProfileContext(connectionId, cwd is null ? defaultWorkingDirectory : Absolute(cwd));
        await authorizeDirectory(context, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var source = await configuration(new("permissionProfile/list", null, context.WorkingDirectory), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var effective = CodexConfigurationResolver.Resolve(source);
        var declarations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (effective.Config.TryGetProperty("permissions", out var permissions))
        {
            if (permissions.ValueKind != JsonValueKind.Object) throw Invalid("Invalid permissions configuration.");
            foreach (var profile in permissions.EnumerateObject())
            {
                ValidateId(profile.Name);
                if (profile.Name.StartsWith(':')) throw Invalid("Configured profiles cannot shadow built-ins.");
                if (declarations.Count >= MaximumProfiles - BuiltIns.Length)
                    throw Invalid("Too many permission profiles.");
                if (profile.Value.ValueKind != JsonValueKind.Object) throw Invalid("Invalid permission profile declaration.");
                declarations.Add(profile.Name, profile.Value.Clone());
            }
        }
        if (source.Requirements is { ValueKind: not JsonValueKind.Object })
            throw Invalid("Invalid permission requirements.");
        return new(context, effective.Revision, declarations, source.Requirements?.Clone());
    }

    private static CodexPermissionProfileCandidate? TryCandidate(Snapshot snapshot, string id)
    {
        var layers = new List<CodexPermissionProfileLayer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        string? description = null, extends = null;
        while (true)
        {
            if (layers.Count >= MaximumInheritanceDepth || !seen.Add(current)) return null;
            if (BuiltIns.Contains(current, StringComparer.Ordinal))
            {
                layers.Add(new(current, null));
                break;
            }
            if (!snapshot.Declarations.TryGetValue(current, out var declaration)) return null;
            if (!TryOptionalText(declaration, "description", 4096, out var text) ||
                !TryOptionalText(declaration, "extends", 128, out var parent) ||
                (parent is not null && !ValidId(parent))) return null;
            if (layers.Count == 0) { description = text; extends = parent; }
            layers.Add(new(current, declaration));
            if (parent is null) break;
            current = parent;
        }
        layers.Reverse();
        return new(snapshot.Context, snapshot.Revision, id, description, extends,
            Array.AsReadOnly(layers.ToArray()), snapshot.Requirements);
    }

    private static string? Description(Snapshot snapshot, string id) =>
        snapshot.Declarations.TryGetValue(id, out var declaration) &&
        TryOptionalText(declaration, "description", 4096, out var text) ? text : null;

    private static bool TryOptionalText(JsonElement value, string property, int maximumLength, out string? text)
    {
        text = null;
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        text = field.GetString();
        return text is not null && text.Length <= maximumLength && !text.Contains('\0');
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
        !id.Any(char.IsControl);

    private static void ValidateId(string? id)
    {
        if (!ValidId(id)) throw Invalid("Invalid permission profile identifier.");
    }

    private static string Absolute(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value))
            throw Invalid("Working directory must be absolute.");
        try { return Path.GetFullPath(value); }
        catch (ArgumentException) { throw Invalid("Invalid working directory."); }
        catch (NotSupportedException) { throw Invalid("Invalid working directory."); }
    }

    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);
}

/// <summary>Source-generated App Server permission profile list serialization metadata.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexPermissionProfileListParams))]
[JsonSerializable(typeof(CodexPermissionProfileListResponse))]
public sealed partial class CodexPermissionProfileJson : JsonSerializerContext;
