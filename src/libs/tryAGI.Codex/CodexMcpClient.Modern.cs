using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Explicit protocol policy for a single host-authorized MCP connection.</summary>
public enum CodexMcpProtocolMode
{
    /// <summary>Use only the legacy initialization lifecycle.</summary>
    Legacy,
    /// <summary>Discover the modern protocol; downgrade only on affirmative legacy evidence.</summary>
    V20260728,
}

/// <summary>Host policy for modern MCP discovery and input capabilities.</summary>
public sealed record CodexMcpProtocolOptions(CodexMcpProtocolMode Mode,
    bool EnableFormElicitation = false, bool EnableUrlElicitation = false);

public sealed partial class CodexMcpClient
{
    /// <summary>Modern stateless MCP wire version.</summary>
    public const string ModernProtocolVersion = "2026-07-28";

    private const string ProtocolVersionMeta = "io.modelcontextprotocol/protocolVersion";
    private const string ClientInfoMeta = "io.modelcontextprotocol/clientInfo";
    private const string ClientCapabilitiesMeta = "io.modelcontextprotocol/clientCapabilities";
    private readonly CodexMcpProtocolOptions protocolOptions = new(CodexMcpProtocolMode.Legacy);
    private JsonObject? modernMetadata;

    /// <summary>Creates a client with an explicitly selected modern/legacy policy.
    /// Form and URL elicitation advertise only host-enabled MRTR capabilities in modern mode;
    /// the actual input responder is supplied per operation.</summary>
    public CodexMcpClient(ICodexMcpTransport transport,
        Func<CodexMcpRequest, CancellationToken, ValueTask<bool>> authorize,
        TimeSpan? requestTimeout, CodexMcpProtocolOptions protocolOptions)
        : this(transport, authorize, requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(protocolOptions);
        if (!Enum.IsDefined(protocolOptions.Mode))
            throw new ArgumentOutOfRangeException(nameof(protocolOptions));
        this.protocolOptions = protocolOptions;
    }

    /// <summary>Resolves the explicit server marker for legacy-compatible stdio startup.
    /// An unmarked helper is not probed by the modern opt-in alone.</summary>
    public static CodexMcpProtocolMode ResolveStdioMode(CodexMcpProtocolMode mode, string? declaredVersion)
    {
        if (mode == CodexMcpProtocolMode.Legacy) return mode;
        if (mode != CodexMcpProtocolMode.V20260728) throw new ArgumentOutOfRangeException(nameof(mode));
        return declaredVersion switch
        {
            null => CodexMcpProtocolMode.Legacy,
            ModernProtocolVersion => mode,
            _ => throw new ArgumentException("Unsupported MCP stdio protocol marker.", nameof(declaredVersion)),
        };
    }

    private async Task<CodexMcpServerInfo> NegotiateAsync(string clientName, string clientVersion,
        CancellationToken cancellationToken)
    {
        if (protocolOptions.Mode == CodexMcpProtocolMode.V20260728)
        {
            var capabilities = new JsonObject();
            if (protocolOptions.EnableFormElicitation || protocolOptions.EnableUrlElicitation)
            {
                var elicitation = new JsonObject();
                if (protocolOptions.EnableFormElicitation) elicitation["form"] = new JsonObject();
                if (protocolOptions.EnableUrlElicitation) elicitation["url"] = new JsonObject();
                capabilities["elicitation"] = elicitation;
            }

            modernMetadata = new JsonObject
            {
                [ProtocolVersionMeta] = ModernProtocolVersion,
                [ClientInfoMeta] = new JsonObject { ["name"] = clientName, ["version"] = clientVersion },
                [ClientCapabilitiesMeta] = capabilities,
            };
            JsonElement? discovery = null;
            try
            {
                discovery = await RequestAsync("server/discover", new JsonObject(), cancellationToken).ConfigureAwait(false);
            }
            catch (CodexMcpRpcException error) when (CanFallback(error))
            {
                // A correlated discovery failure is not an ambiguous tool result.
            }

            if (discovery is { } result)
            {
                if (result.ValueKind != JsonValueKind.Object ||
                    CodexRealtimeProtocol.Text(result, "resultType") != "complete" ||
                    !result.TryGetProperty("supportedVersions", out var versions) ||
                    versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0 ||
                    !result.TryGetProperty("capabilities", out var advertised) || advertised.ValueKind != JsonValueKind.Object)
                    throw new CodexProtocolException("Invalid MCP discovery result.");
                var supported = versions.EnumerateArray().ToArray();
                if (supported.Any(version => version.ValueKind != JsonValueKind.String))
                    throw new CodexProtocolException("Invalid MCP discovery versions.");
                if (supported.Any(version => version.GetString() == ModernProtocolVersion))
                {
                    SetTransportProtocol(ModernProtocolVersion);
                    return new(ModernProtocolVersion, advertised.Clone(), result.Clone());
                }
                if (!OnlyKnownLegacyVersions(versions))
                    throw new CodexProtocolException("MCP discovery did not establish a supported protocol.");
            }
            modernMetadata = null;
        }

        var legacy = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = LegacyProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = clientVersion },
        }, cancellationToken).ConfigureAwait(false);
        var selected = legacy.ValueKind == JsonValueKind.Object ? CodexRealtimeProtocol.Text(legacy, "protocolVersion") : null;
        if (selected is null ||
            !(selected == LegacyProtocolVersion || protocolOptions.Mode == CodexMcpProtocolMode.V20260728 && IsKnownLegacyVersion(selected)) ||
            !legacy.TryGetProperty("capabilities", out var legacyCapabilities) || legacyCapabilities.ValueKind != JsonValueKind.Object ||
            !legacy.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new CodexProtocolException("Unsupported or invalid MCP initialize result.");
        SetTransportProtocol(selected);
        using var initialized = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop);
        initialized.CancelAfter(timeout);
        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized",
        }, initialized.Token).ConfigureAwait(false);
        return new(selected, legacyCapabilities.Clone(), legacy.Clone());
    }

    private void SetTransportProtocol(string version)
    {
        if (transport is CodexMcpHttpTransport http) http.SetNegotiatedProtocol(version);
    }

    private void ApplyModernMetadata(JsonObject parameters)
    {
        if (modernMetadata is null) return;
        var metadata = parameters["_meta"] switch
        {
            null => new JsonObject(),
            JsonObject existing => existing,
            _ => throw new ArgumentException("MCP metadata must be a JSON object."),
        };
        foreach (var field in modernMetadata)
        {
            if (metadata.TryGetPropertyValue(field.Key, out var supplied) && !JsonNode.DeepEquals(supplied, field.Value))
                throw new UnauthorizedAccessException("MCP protocol metadata cannot be overridden.");
            metadata[field.Key] = field.Value?.DeepClone();
        }
        if (metadata.Parent is null) parameters["_meta"] = metadata;
    }

    private static bool CanFallback(CodexMcpRpcException error)
    {
        if (error.Code == -32601) return true;
        if (error.Code is not (-32022 or -32600 or -32602)) return false;
        if (error.DataValue is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("supported", out var supported))
            return OnlyKnownLegacyVersions(supported);
        return error.Message.Equals("Unsupported protocol version: " + ModernProtocolVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool OnlyKnownLegacyVersions(JsonElement versions) =>
        versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0 &&
        versions.EnumerateArray().All(version => version.ValueKind == JsonValueKind.String &&
            IsKnownLegacyVersion(version.GetString()));

    internal static bool IsKnownLegacyVersion(string? version) =>
        version is "2024-11-05" or "2025-03-26" or LegacyProtocolVersion or "2025-11-25";

    private async Task<JsonElement> InvokeModernCompleteAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var result = await RequestAsync(method, parameters, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || CodexRealtimeProtocol.Text(result, "resultType") != "complete")
            throw new CodexProtocolException("MCP modern request did not return a complete result; explicit input continuation is required.");
        return result;
    }
}
