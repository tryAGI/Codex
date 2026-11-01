using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Metadata extraction for host-trusted OAuth tokens, not JWT authentication or signature validation.</summary>
internal sealed record CodexIdentityClaims(string? AccountId, string? UserId, bool IsFedRampAccount)
{
    internal static CodexIdentityClaims? Read(string jwt)
    {
        if (jwt.Length > 131072) return null;
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var part = parts[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight((part.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(part));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) ||
                auth.ValueKind != JsonValueKind.Object) return null;
            var restricted = false;
            if (auth.TryGetProperty("chatgpt_account_is_fedramp", out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                restricted = flag.GetBoolean();
            }
            return new(CodexRealtimeProtocol.Text(auth, "chatgpt_account_id"),
                CodexRealtimeProtocol.Text(auth, "chatgpt_user_id") ?? CodexRealtimeProtocol.Text(auth, "user_id"), restricted);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    public override string ToString() => "CodexIdentityClaims [redacted]";
}
