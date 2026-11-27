namespace tryAGI.Codex;

// Parse only the Git remote address; never execute helpers or normalize repository paths.
internal static class CodexGitRemote
{
    internal static string Sanitize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = input.AsSpan();
        if (value.IsEmpty || input.Any(char.IsControl)) throw Invalid();
        var helperLength = 0;
        while (true)
        {
            var separator = value.IndexOf("::", StringComparison.Ordinal);
            if (separator <= 0 || !IsScheme(value[..separator])) break;
            // A helper payload can be a command line, not an address.
            if (helperLength == 0 && input.Any(char.IsWhiteSpace)) throw Invalid();
            if (value[..separator].Equals("ext", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            helperLength += separator + 2;
            value = value[(separator + 2)..];
        }

        var remote = value.ToString();
        if (remote.Length == 0) throw Invalid();
        var urlSeparator = remote.IndexOf("://", StringComparison.Ordinal);
        if (urlSeparator > 0)
        {
            var scheme = remote[..urlSeparator];
            if (!IsScheme(scheme.AsSpan())) throw Invalid();
            var tail = remote[(urlSeparator + 3)..];
            var http = scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            var boundary = http ? tail.IndexOfAny(['/', '?', '#']) : tail.IndexOf('/');
            if (boundary < 0) boundary = tail.Length;
            var authority = tail[..boundary];
            var path = tail[boundary..];
            var at = authority.LastIndexOf('@');
            var host = authority[(at + 1)..];
            var file = scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
            if (host.Length == 0 && (!file || at >= 0)) throw Invalid();
            if (host.Length > 0 && !ValidAuthority(host)) throw Invalid();
            var git = scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase) && at > 0
                && (authority[..at] == "git" || authority[..at].StartsWith("git:", StringComparison.Ordinal));
            return input[..helperLength] + scheme + "://" + (git ? "git@" : "") + host + path;
        }

        // Git's SCP form is [user@]host:path. A colon after a path separator is local.
        var firstSlash = remote.IndexOfAny(['/', '\\']);
        var firstColon = remote.IndexOf(':');
        var userEnd = remote.IndexOf('@');
        var hostStart = userEnd >= 0 && (firstSlash < 0 || userEnd < firstSlash)
            && (firstColon < 0 || userEnd < firstColon) ? userEnd + 1 : 0;
        int colon;
        if (hostStart < remote.Length && remote[hostStart] == '[')
        {
            var close = remote.IndexOf(']', hostStart + 1);
            colon = close >= 0 && close + 1 < remote.Length && remote[close + 1] == ':' ? close + 1 : -1;
            if (colon < 0) throw Invalid();
        }
        else colon = remote.IndexOf(':', hostStart);
        if (colon >= 0 && (firstSlash < 0 || colon < firstSlash))
        {
            if (colon == remote.Length - 1 || !ValidAuthority(remote[hostStart..colon])) throw Invalid();
            if (hostStart > 0 && remote[..userEnd] != "git")
                return input[..helperLength] + remote[hostStart..];
            return input;
        }
        // Reject ambiguous user-like local names rather than persisting a possible credential.
        if (hostStart > 0 || helperLength != 0) throw Invalid();
        return input;
    }

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var ch in value)
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('+' or '-' or '.')) return false;
        return true;
    }

    private static bool ValidAuthority(string value)
        => value.Length > 0 && !value.Any(char.IsWhiteSpace) && value.IndexOfAny(['@', '/', '\\', '?', '#', '%']) < 0
            && Uri.TryCreate("https://" + value + "/", UriKind.Absolute, out var parsed)
            && parsed.Host.Length > 0 && parsed.UserInfo.Length == 0;

    private static ArgumentException Invalid() => new("gitInfo.originUrl must be a valid Git remote.");
}
