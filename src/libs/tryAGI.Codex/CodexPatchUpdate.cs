using System.Text;

namespace tryAGI.Codex;

/// <summary>How <see cref="CodexPatch.Update"/> reconstructs line endings.</summary>
public enum CodexPatchUpdateMode
{
    /// <summary>Legacy behaviour of Codex releases before <c>rust-v0.166.0</c>: lines are split on LF and
    /// rejoined with LF; stray CR characters may remain. Kept only to reproduce older results.</summary>
    NormalizeToLf,

    /// <summary>Current upstream behaviour: keep each original line terminator (LF, CRLF or CR) and the
    /// file's dominant one for new lines.</summary>
    PreserveLineEndings,
}

public static partial class CodexPatch
{
    private sealed record Replacement(int Start, int Count, IReadOnlyList<string> Lines);
    private sealed record SourceLine(string Text, string? Ending);
    /// <summary>Largest source text, in UTF-16 characters, <see cref="Update"/> accepts.</summary>
    public const int MaximumSourceCharacters = 4 * 1024 * 1024;

    /// <summary>Derive intended contents without filesystem effects. The host must still
    /// authorize paths, compare the original byte hash at dispatch, and write atomically.</summary>
    public static string Update(string original, CodexPatchHunk hunk, CodexPatchUpdateMode mode = CodexPatchUpdateMode.PreserveLineEndings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(hunk);
        cancellationToken.ThrowIfCancellationRequested();
        if (original.Length > MaximumSourceCharacters) throw new CodexPatchException("Source exceeds the bounded text budget.");
        ValidateText(original);
        if (hunk.Kind != CodexPatchKind.Update || hunk.Chunks.Count == 0) throw new CodexPatchException("An update hunk with chunks is required.");
        if (mode is not (CodexPatchUpdateMode.NormalizeToLf or CodexPatchUpdateMode.PreserveLineEndings)) throw new ArgumentOutOfRangeException(nameof(mode));
        List<SourceLine> source;
        string preferred;
        if (mode == CodexPatchUpdateMode.NormalizeToLf)
        {
            var split = original.Split('\n').ToList();
            if (split[^1].Length == 0) split.RemoveAt(split.Count - 1);
            source = split.Select(t => new SourceLine(t, "\n")).ToList(); preferred = "\n";
        }
        else (source, preferred) = SplitSource(original);
        var lines = source.Select(x => x.Text).ToArray();
        var replacements = Compute(lines, hunk, mode, cancellationToken);
        if (mode == CodexPatchUpdateMode.NormalizeToLf)
        {
            var output = lines.ToList();
            foreach (var change in replacements.AsEnumerable().Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Start > output.Count) throw new CodexPatchException("Update chunks exceed the remaining source lines.");
                // Rust's legacy reconstruction removes available lines one at a time;
                // EOF lookup can revisit an earlier region after a later chunk.
                output.RemoveRange(change.Start, Math.Min(change.Count, output.Count - change.Start)); output.InsertRange(change.Start, change.Lines);
            }
            if (output.Count == 0 || output[^1].Length != 0) output.Add("");
            var result = string.Join('\n', output);
            if (result.Length > MaximumSourceCharacters) throw new CodexPatchException("Updated source exceeds the bounded text budget.");
            return result;
        }
        var builder = new StringBuilder();
        var position = 0;
        void AppendLine(SourceLine line) { builder.Append(line.Text).Append(line.Ending ?? preferred); }
        foreach (var change in replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Start < position) throw new CodexPatchException("Overlapping update chunks cannot preserve source lines safely.");
            while (position < change.Start) AppendLine(source[position++]);
            position += change.Count;
            foreach (var line in change.Lines) AppendLine(new(line, preferred));
            if (builder.Length > MaximumSourceCharacters) throw new CodexPatchException("Updated source exceeds the bounded text budget.");
        }
        while (position < source.Count) AppendLine(source[position++]);
        if (builder.Length > MaximumSourceCharacters) throw new CodexPatchException("Updated source exceeds the bounded text budget.");
        return builder.ToString();
    }

    private static (List<SourceLine>, string) SplitSource(string original)
    {
        var lines = new List<SourceLine>(); string? preferred = null;
        var start = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] is not ('\r' or '\n')) continue;
            var ending = original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n' ? "\r\n" : original[i].ToString();
            preferred ??= ending; lines.Add(new(original[start..i], ending));
            i += ending.Length - 1; start = i + 1;
        }
        if (start < original.Length) lines.Add(new(original[start..], null));
        return (lines, preferred ?? "\n");
    }

    private static List<Replacement> Compute(string[] lines, CodexPatchHunk hunk, CodexPatchUpdateMode mode, CancellationToken cancellationToken)
    {
        var changes = new List<Replacement>(); var index = 0;
        foreach (var chunk in hunk.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.ChangeContext is { } context)
            {
                var foundContext = Seek(lines, [context], index, false, mode, cancellationToken);
                if (foundContext is null) throw new CodexPatchException($"Failed to find context '{context}' in {hunk.Path}");
                index = foundContext.Value + 1;
            }
            if (chunk.OldLines.Count == 0)
            {
                changes.Add(new(mode == CodexPatchUpdateMode.NormalizeToLf && lines.LastOrDefault() == "" ? lines.Length - 1 : lines.Length, 0, chunk.NewLines));
                continue;
            }
            IReadOnlyList<string> pattern = chunk.OldLines, replacement = chunk.NewLines;
            var match = Seek(lines, pattern, index, chunk.EndOfFile, mode, cancellationToken);
            if (match is null && pattern[^1] == "")
            {
                pattern = pattern.Take(pattern.Count - 1).ToArray();
                if (replacement.LastOrDefault() == "") replacement = replacement.Take(replacement.Count - 1).ToArray();
                match = Seek(lines, pattern, index, chunk.EndOfFile, mode, cancellationToken);
            }
            if (match is null) throw new CodexPatchException($"Failed to find expected lines in {hunk.Path}:\n{string.Join('\n', chunk.OldLines)}");
            if (mode == CodexPatchUpdateMode.NormalizeToLf) changes.Add(new(match.Value, pattern.Count, replacement));
            else
            {
                var oldStart = 0; var newStart = 0;
                foreach (var pair in chunk.ContextLines)
                {
                    if (pair.OldIndex < oldStart || pair.NewIndex < newStart) throw new CodexPatchException("Invalid context-line indices.");
                    if (pair.OldIndex >= pattern.Count || pair.NewIndex >= replacement.Count) break;
                    if (oldStart != pair.OldIndex || newStart != pair.NewIndex)
                        changes.Add(new(match.Value + oldStart, pair.OldIndex - oldStart, replacement.Skip(newStart).Take(pair.NewIndex - newStart).ToArray()));
                    oldStart = pair.OldIndex + 1; newStart = pair.NewIndex + 1;
                }
                if (oldStart != pattern.Count || newStart != replacement.Count)
                    changes.Add(new(match.Value + oldStart, pattern.Count - oldStart, replacement.Skip(newStart).ToArray()));
            }
            index = match.Value + pattern.Count;
        }
        return changes.OrderBy(r => r.Start).ToList(); // Stable order for equal-position insertions.
    }

    private static int? Seek(string[] lines, IReadOnlyList<string> pattern, int start, bool eof, CodexPatchUpdateMode mode, CancellationToken cancellationToken)
    {
        if (pattern.Count == 0) return start;
        if (pattern.Count > lines.Length) return null;
        var first = eof ? mode == CodexPatchUpdateMode.NormalizeToLf ? lines.Length - pattern.Count : Math.Max(start, lines.Length - pattern.Count) : start;
        if (first > lines.Length - pattern.Count) return null;
        static string CompareText(string value, int pass) => pass switch { 0 => value, 1 => value.TrimEnd(), 2 => value.Trim(), _ => Normalize(value) };
        for (var tolerance = 0; tolerance < 4; tolerance++)
        {
            // KMP over logical lines keeps the same pass priority and earliest match
            // as Rust, without quadratic repeated-prefix scans that stall workers.
            var tokens = pattern.Select(p => CompareText(p, tolerance)).ToArray();
            var prefix = new int[tokens.Length];
            for (var i = 1; i < tokens.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var matched = prefix[i - 1];
                while (matched > 0 && tokens[i] != tokens[matched]) matched = prefix[matched - 1];
                if (tokens[i] == tokens[matched]) matched++;
                prefix[i] = matched;
            }
            var length = 0;
            for (var i = first; i < lines.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var token = CompareText(lines[i], tolerance);
                while (length > 0 && token != tokens[length]) length = prefix[length - 1];
                if (token == tokens[length]) length++;
                if (length == tokens.Length) return i - length + 1;
            }
        }
        return null;
    }

    private static string Normalize(string value) => new(value.Trim().Select(c => c switch
    {
        '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
        '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
        '\u201c' or '\u201d' or '\u201e' or '\u201f' => '"',
        '\u00a0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200a' or '\u202f' or '\u205f' or '\u3000' => ' ',
        _ => c
    }).ToArray());
}
