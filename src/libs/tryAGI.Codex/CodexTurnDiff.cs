using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

/// <summary>Net content diff from verified worker captures. Never parses model claims as effects.</summary>
internal sealed class CodexTurnDiff
{
    private const int MaximumBytes = 524_288;
    private readonly Dictionary<string, CodexWorkspaceDiffFile> files = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private string? root;

    public string Apply(CodexWorkspacePatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.DryRun) throw new ArgumentException("A preview is not an applied change.");
        lock (gate)
        {
            if (root is not null && root != result.Approval.Root) throw new ArgumentException("Diff workspace changed.");
            var captures = result.DiffFiles ?? throw new ArgumentException("Worker did not supply verified diff captures.");
            if (captures.Count != result.Files.Count || captures.Count > 256) throw new ArgumentException("Incomplete diff captures.");
            if (captures.Sum(x => (long)Encoding.UTF8.GetByteCount(x.BeforeText ?? "") + Encoding.UTF8.GetByteCount(x.AfterText ?? "")) > MaximumBytes)
                throw new ArgumentException("Incoming diff capture budget exceeded.");
            var next = new Dictionary<string, CodexWorkspaceDiffFile>(files, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capture in captures)
            {
                if (string.IsNullOrWhiteSpace(capture.Path) || capture.Path.Any(char.IsControl) || !seen.Add(capture.Path))
                    throw new ArgumentException("Invalid diff path identity.");
                var receipt = result.Files.SingleOrDefault(x => x?.Path == capture.Path) ?? throw new ArgumentException("Diff receipt missing.");
                if (Hash(capture.BeforeText) != receipt.BeforeSha256 || Hash(capture.AfterText) != receipt.AfterSha256)
                    throw new ArgumentException("Diff bytes do not match applied receipt hashes.");
                if (next.TryGetValue(capture.Path, out var previous))
                {
                    if (previous.AfterText != capture.BeforeText) throw new ArgumentException("Diff chain changed outside this turn; no incomplete aggregate emitted.");
                    next[capture.Path] = capture with { BeforeText = previous.BeforeText };
                }
                else next.Add(capture.Path, capture);
            }
            if (next.Count > 256 || next.Values.Sum(x => (long)Encoding.UTF8.GetByteCount(x.BeforeText ?? "") + Encoding.UTF8.GetByteCount(x.AfterText ?? "")) > MaximumBytes)
                throw new ArgumentException("Turn diff capture budget exceeded.");
            var output = new StringBuilder();
            foreach (var file in next.Values.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                if (file.BeforeText == file.AfterText) continue;
                var before = Lines(file.BeforeText); var after = Lines(file.AfterText);
                output.Append("diff --git ").Append(Quote("a/" + file.Path)).Append(' ').Append(Quote("b/" + file.Path)).Append('\n');
                output.Append("--- ").Append(file.BeforeText is null ? "/dev/null" : Quote("a/" + file.Path)).Append('\n');
                output.Append("+++ ").Append(file.AfterText is null ? "/dev/null" : Quote("b/" + file.Path)).Append('\n');
                output.Append("@@ -").Append(before.Length == 0 ? 0 : 1).Append(',').Append(before.Length)
                    .Append(" +").Append(after.Length == 0 ? 0 : 1).Append(',').Append(after.Length).Append(" @@\n");
                Append(output, before, '-', file.BeforeText); Append(output, after, '+', file.AfterText);
            }
            if (Encoding.UTF8.GetByteCount(output.ToString()) > 1_000_000) throw new ArgumentException("Turn diff notification exceeds its bound.");
            files.Clear(); foreach (var pair in next) files.Add(pair.Key, pair.Value); root = result.Approval.Root;
            return output.ToString();
        }
    }
    private static string? Hash(string? text) => text is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Split('\n'); return text.EndsWith('\n') ? lines[..^1] : lines;
    }
    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static void Append(StringBuilder output, string[] lines, char prefix, string? source)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            output.Append(prefix).Append(lines[i]).Append('\n');
            if (i == lines.Length - 1 && source is not null && !source.EndsWith('\n')) output.Append("\\ No newline at end of file\n");
        }
    }
}
