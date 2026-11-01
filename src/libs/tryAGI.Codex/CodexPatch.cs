using System.Text;

namespace tryAGI.Codex;

public enum CodexPatchKind { Add, Delete, Update }
public sealed record CodexPatchContextLine(int OldIndex, int NewIndex);
public sealed record CodexPatchChunk(string? ChangeContext, IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines, IReadOnlyList<CodexPatchContextLine> ContextLines, bool EndOfFile);
public sealed record CodexPatchHunk(CodexPatchKind Kind, string Path, string? MoveTo, string? Content,
    IReadOnlyList<CodexPatchChunk> Chunks);
public sealed record CodexPatchDocument(IReadOnlyList<CodexPatchHunk> Hunks, string? EnvironmentId);

public sealed class CodexPatchException(string message, int? lineNumber = null) : ArgumentException(message)
{
    public int? LineNumber { get; } = lineNumber;
}

/// <summary>Codex's marker-based apply_patch format (not a git/unified-diff parser).
/// Pure text operations: paths/environment IDs are data, never filesystem authorization.</summary>
public static partial class CodexPatch
{
    public const int MaximumPatchCharacters = 600_000;
    internal static void ValidateText(string text)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw new CodexPatchException("Patch/source text must contain valid Unicode."); }
    }

    public static CodexPatchDocument Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Length > MaximumPatchCharacters) throw new CodexPatchException("Patch exceeds the bounded text budget.");
        ValidateText(patch);
        var lines = patch.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        static bool Boundaries(string[] input) => input.Length >= 2 && input[0].Trim() == "*** Begin Patch" && input[^1].Trim() == "*** End Patch";
        if (!Boundaries(lines) && lines.Length >= 4 && lines[0] is "<<EOF" or "<<'EOF'" or "<<\"EOF\"" && lines[^1].EndsWith("EOF", StringComparison.Ordinal))
            lines = lines[1..^1]; // Literal heredoc wrapper, never evaluated by a shell.
        if (!Boundaries(lines))
            throw new CodexPatchException(lines[0].Trim() != "*** Begin Patch"
                ? "The first line of the patch must be '*** Begin Patch'" : "The last line of the patch must be '*** End Patch'");
        var parser = new CodexPatchParser();
        parser.Append(string.Join('\n', lines));
        return parser.Finish();
    }
}

/// <summary>Incremental marker parser. A snapshot never authorizes or dispatches writes;
/// call Finish before compiling a plan. Chunk boundaries may split CRLF or surrogate pairs.</summary>
public sealed class CodexPatchParser
{
    private enum Mode { NotStarted, Started, Add, Delete, Update, Ended }
    private sealed class Chunk
    {
        internal string? Context;
        internal readonly List<string> Old = [], New = [];
        internal readonly List<CodexPatchContextLine> ContextLines = [];
        internal bool Eof;
        internal bool Empty => Old.Count == 0 && New.Count == 0;
        internal void AddContext(string line) { ContextLines.Add(new(Old.Count, New.Count)); Old.Add(line); New.Add(line); }
    }
    private sealed class Hunk(CodexPatchKind kind, string path, int line)
    {
        internal readonly CodexPatchKind Kind = kind;
        internal readonly string Path = path;
        internal readonly int Line = line;
        internal string? Move;
        internal readonly StringBuilder Content = new();
        internal readonly List<Chunk> Chunks = [];
        internal CodexPatchHunk Snapshot() => new(Kind, Path, Move, Kind == CodexPatchKind.Add ? Content.ToString() : null,
            Array.AsReadOnly(Chunks.Select(c => new CodexPatchChunk(c.Context, Array.AsReadOnly(c.Old.ToArray()),
                Array.AsReadOnly(c.New.ToArray()), Array.AsReadOnly(c.ContextLines.ToArray()), c.Eof)).ToArray()));
    }
    private readonly StringBuilder buffer = new();
    private readonly List<Hunk> hunks = [];
    private Mode mode;
    private int lineNumber, characters;
    private string? environmentId;
    private bool failed;
    public CodexPatchDocument Snapshot => new(Array.AsReadOnly(hunks.Select(h => h.Snapshot()).ToArray()), environmentId);

    public void Append(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (failed) throw new CodexPatchException("Parser has failed; create a new parser.");
        if (delta.Length > CodexPatch.MaximumPatchCharacters - characters) { failed = true; throw new CodexPatchException("Patch exceeds the bounded text budget."); }
        characters += delta.Length;
        try
        {
            foreach (var character in delta)
            {
                if (character != '\n') { buffer.Append(character); continue; }
                var line = buffer.ToString(); buffer.Clear(); lineNumber++;
                if (line.EndsWith('\r')) line = line[..^1];
                CodexPatch.ValidateText(line); Process(line);
            }
        }
        catch { failed = true; throw; }
    }

    public CodexPatchDocument Finish()
    {
        if (failed) throw new CodexPatchException("Parser has failed; create a new parser.");
        try
        {
            if (buffer.Length > 0)
            {
                var line = buffer.ToString(); buffer.Clear(); lineNumber++; CodexPatch.ValidateText(line);
                if (line.Trim() == "*** End Patch") { EnsureUpdate(line.Trim()); mode = Mode.Ended; }
                else Process(line);
            }
            if (mode != Mode.Ended) throw new CodexPatchException("The last line of the patch must be '*** End Patch'");
            return Snapshot;
        }
        catch { failed = true; throw; }
    }

    private CodexPatchException Invalid(string message) => new(message, lineNumber);
    private void EnsureUpdate(string line)
    {
        if (mode != Mode.Update) return;
        var hunk = hunks[^1];
        if (hunk.Chunks.Count == 0) throw new CodexPatchException($"Update file hunk for path '{hunk.Path}' is empty", hunk.Line);
        if (hunk.Chunks[^1].Empty) throw Invalid(line == "*** End Patch" ? "Update hunk does not contain any lines" : $"Unexpected line found in update hunk: '{line}'. Every line should start with ' ' (context line), '+' (added line), or '-' (removed line)");
    }

    private bool Header(string line)
    {
        if (mode == Mode.Started && line.StartsWith("*** Environment ID:", StringComparison.Ordinal))
        {
            if (environmentId is not null) throw new CodexPatchException("apply_patch environment_id cannot be specified more than once");
            environmentId = line[19..].Trim();
            if (environmentId.Length == 0) throw new CodexPatchException("apply_patch environment_id cannot be empty");
            return true;
        }
        if (line == "*** End Patch") { EnsureUpdate(line); mode = Mode.Ended; return true; }
        foreach (var (prefix, kind, next) in new[] { ("*** Add File: ", CodexPatchKind.Add, Mode.Add),
            ("*** Delete File: ", CodexPatchKind.Delete, Mode.Delete), ("*** Update File: ", CodexPatchKind.Update, Mode.Update) })
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            EnsureUpdate(line); hunks.Add(new(kind, line[prefix.Length..], lineNumber)); mode = next; return true;
        }
        return false;
    }

    private void Process(string line)
    {
        var trimmed = line.Trim();
        if (mode == Mode.NotStarted)
        {
            if (trimmed != "*** Begin Patch") throw new CodexPatchException("The first line of the patch must be '*** Begin Patch'");
            mode = Mode.Started; return;
        }
        if (mode == Mode.Ended)
        {
            if (trimmed.Length != 0) throw new CodexPatchException("The last line of the patch must be '*** End Patch'");
            return;
        }
        if (mode != Mode.Update)
        {
            if (Header(trimmed)) return;
            if (mode == Mode.Add && line.StartsWith('+')) { hunks[^1].Content.Append(line.AsSpan(1)).Append('\n'); return; }
            throw Invalid($"'{trimmed}' is not a valid hunk header. Valid hunk headers: '*** Add File: {{path}}', '*** Delete File: {{path}}', '*** Update File: {{path}}'");
        }
        var updateLine = line.TrimEnd();
        if (Header(updateLine)) return;
        var update = hunks[^1];
        var chunks = update.Chunks;
        if (chunks.LastOrDefault()?.Eof == true)
        {
            if (updateLine.Length == 0) return;
            if (updateLine != "@@" && !updateLine.StartsWith("@@ ", StringComparison.Ordinal))
                throw Invalid($"Expected update hunk to start with a @@ context marker, got: '{line}'");
        }
        if (chunks.Count == 0 && update.Move is null && updateLine.StartsWith("*** Move to: ", StringComparison.Ordinal))
        { update.Move = updateLine[13..]; return; }
        if (updateLine == "@@" || updateLine.StartsWith("@@ ", StringComparison.Ordinal))
        {
            if (chunks.LastOrDefault()?.Empty == true) throw Invalid($"Unexpected line found in update hunk: '{line}'. Every line should start with ' ' (context line), '+' (added line), or '-' (removed line)");
            chunks.Add(new() { Context = updateLine == "@@" ? null : updateLine[3..] }); return;
        }
        if (updateLine == "*** End of File")
        {
            if (chunks.LastOrDefault()?.Empty == true) throw Invalid("Update hunk does not contain any lines");
            if (chunks.Count > 0) chunks[^1].Eof = true;
            return;
        }
        if (line.Length == 0 || line[0] is ' ' or '+' or '-')
        {
            if (chunks.Count == 0) chunks.Add(new());
            if (line.Length == 0) chunks[^1].AddContext("");
            else if (line[0] == ' ') chunks[^1].AddContext(line[1..]);
            else if (line[0] == '+') chunks[^1].New.Add(line[1..]);
            else chunks[^1].Old.Add(line[1..]);
            return;
        }
        throw Invalid(chunks.LastOrDefault() is { Empty: false }
            ? $"Expected update hunk to start with a @@ context marker, got: '{line}'"
            : $"Unexpected line found in update hunk: '{line}'. Every line should start with ' ' (context line), '+' (added line), or '-' (removed line)");
    }
}
