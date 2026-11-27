using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Bounded source-preserving edits using the existing TOML parser's statement spans.
/// Unsupported source shapes fail before a file write; the entire result must parse to the exact candidate.</summary>
internal static class CodexTomlEdit
{
    private sealed record Change(int Start, int End, string Text);

    internal static string Rewrite(string text, JsonObject candidate)
    {
        var source = CodexToml.ParseWithSpans(text);
        var changes = new List<Change>();
        var insertions = new Dictionary<int, StringBuilder>();
        var appendedSections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        void Insert(string[] path, JsonNode value)
        {
            var parent = path[..^1];
            var section = source.Sections.Where(item => Prefix(item.Path, parent))
                .OrderByDescending(item => item.Path.Length).First();
            // If the parent is implicit from a child header, opening it explicitly avoids
            // redefining a table via a dotted key in a different section.
            if (section.Path.Length < parent.Length &&
                source.Sections.Any(item => item.Path.Length > parent.Length && Prefix(parent, item.Path)))
            {
                var header = Dotted(parent);
                if (!appendedSections.TryGetValue(header, out var lines))
                    appendedSections.Add(header, lines = new StringBuilder());
                lines.Append(Quote(path[^1])).Append(" = ").Append(Format(value)).Append(newline);
                return;
            }
            if (!insertions.TryGetValue(section.InsertAt, out var buffer))
                insertions.Add(section.InsertAt, buffer = new StringBuilder());
            buffer.Append(Dotted(path[section.Path.Length..])).Append(" = ").Append(Format(value)).Append(newline);
        }

        void Plan(string[] path, JsonNode? before, JsonNode? after)
        {
            if (JsonNode.DeepEquals(before, after)) return;
            var assignment = source.Assignments.SingleOrDefault(item => item.Path.SequenceEqual(path, StringComparer.Ordinal));
            if (assignment is not null)
            {
                var original = text.AsSpan(assignment.ValueStart, assignment.ValueEnd - assignment.ValueStart);
                // Do not discard comments within an edited multi-line inline value.
                if (before is JsonObject or JsonArray && (original.Contains('#') || original.Contains('\n')))
                    throw Unsupported();
                changes.Add(after is null
                    ? new(assignment.Start, assignment.ValueEnd, "")
                    : new(assignment.ValueStart, assignment.ValueEnd, Format(after, before)));
                return;
            }
            if (before is null && after is not null)
            {
                Insert(path, after);
                return;
            }
            if (before is not JsonObject oldTable || after is not JsonObject newTable)
                throw Unsupported();
            foreach (var field in oldTable)
                Plan([.. path, field.Key], field.Value, newTable[field.Key]);
            foreach (var field in newTable.Where(item => !oldTable.ContainsKey(item.Key)))
                Plan([.. path, field.Key], null, field.Value);
            // A table previously defined only by its dotted children vanishes when the last
            // child is removed. Preserve the candidate's empty table explicitly.
            if (path.Length > 0 && oldTable.Count > 0 && newTable.Count == 0 &&
                !source.Sections.Any(item => Prefix(path, item.Path)))
                Insert(path, new JsonObject());
        }

        foreach (var field in source.Root) Plan([field.Key], field.Value, candidate[field.Key]);
        foreach (var field in candidate.Where(item => !source.Root.ContainsKey(item.Key)))
            Plan([field.Key], null, field.Value);

        foreach (var insertion in insertions)
        {
            var prefix = insertion.Key > 0 && text[insertion.Key - 1] != '\n' ? newline : "";
            changes.Add(new(insertion.Key, insertion.Key, prefix + insertion.Value));
        }
        if (appendedSections.Count > 0)
        {
            var buffer = new StringBuilder(newline);
            foreach (var (header, lines) in appendedSections)
                buffer.Append('[').Append(header).Append(']').Append(newline).Append(lines);
            changes.Add(new(text.Length, text.Length, buffer.ToString()));
        }

        // Apply from the end. At a shared start, replace/delete the old statement before insertion.
        var result = new StringBuilder(text);
        var boundary = text.Length;
        foreach (var change in changes.OrderByDescending(item => item.Start).ThenByDescending(item => item.End))
        {
            if (change.Start < 0 || change.End > boundary || change.End < change.Start) throw Unsupported();
            result.Remove(change.Start, change.End - change.Start);
            result.Insert(change.Start, change.Text);
            boundary = change.Start;
        }
        var output = result.ToString();
        try
        {
            if (!JsonNode.DeepEquals(CodexToml.Parse(output), candidate)) throw Unsupported();
        }
        catch (FormatException) { throw Unsupported(); }
        return output;
    }

    private static bool Prefix(string[] prefix, string[] path) => prefix.Length <= path.Length &&
        prefix.SequenceEqual(path.Take(prefix.Length), StringComparer.Ordinal);

    private static string Dotted(IEnumerable<string> path) => string.Join(".", path.Select(Quote));

    private static string Format(JsonNode node, JsonNode? before = null)
    {
        // Preserve the original TOML numeric type when an untouched sibling is inside
        // an edited one-line inline table. JSON equality alone ignores 1 versus 1.0.
        if (before is not null && JsonNode.DeepEquals(before, node)) node = before;
        if (node is JsonObject table)
            return "{ " + string.Join(", ", table.Select(item => Quote(item.Key) + " = " +
                Format(item.Value ?? throw Unsupported(), (before as JsonObject)?[item.Key]))) + " }";
        if (node is JsonArray array)
            return "[" + string.Join(", ", array.Select((item, index) => Format(item ?? throw Unsupported(),
                before is JsonArray old && index < old.Count ? old[index] : null))) + "]";
        return node.GetValueKind() switch
        {
            JsonValueKind.String => Quote(node.GetValue<string>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => Number(node),
            _ => throw Unsupported()
        };
    }

    private static string Number(JsonNode node)
    {
        var raw = node.ToJsonString();
        if (node is JsonValue value && value.TryGetValue<double>(out _) && !value.TryGetValue<long>(out _) &&
            !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) raw += ".0";
        return raw;
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var ch in value)
            text.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' or '\u007f' => "\\u" + ((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
                _ => ch.ToString()
            });
        return text.Append('"').ToString();
    }

    private static CodexConfigurationWriteException Unsupported() => new(CodexConfigurationWriteFailure.ConfigValidationError);
}
