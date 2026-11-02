using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace tryAGI.Codex;

/// <summary>
/// Dependency-free TOML 1.0 reader for Codex configuration files, producing JSON.
/// </summary>
/// <remarks>
/// Supports tables, arrays of tables, dotted and quoted keys, inline tables, arrays, all four
/// string forms, integers (decimal, hex, octal, binary), finite floats and booleans, and
/// enforces the specification's rules against redefining keys and tables. Offset dates, local
/// dates and times, <c>inf</c> and <c>nan</c> have no JSON form and are rejected, as is anything
/// else outside the grammar. As in TOML 1.1, inline tables may span lines and end with a comma.
/// </remarks>
internal static partial class CodexToml
{
    private const int MaximumDepth = 32;

    /// <summary>Parses a TOML document.</summary>
    /// <param name="text">Document text, without a byte order mark.</param>
    /// <returns>The root table.</returns>
    /// <exception cref="FormatException">The document is not valid TOML or uses an unsupported value.</exception>
    public static JsonObject Parse(string text) => new Parser(text).Document();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9](?:_?[0-9])*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalInteger();

    [GeneratedRegex(@"^0(?:x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|o[0-7](?:_?[0-7])*|b[01](?:_?[01])*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedInteger();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9](?:_?[0-9])*)(?:\.[0-9](?:_?[0-9])*)?(?:[eE][+-]?[0-9](?:_?[0-9])*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Float();

    private sealed class Parser(string text)
    {
        // Reference sets: TOML's redefinition rules depend on how a table came to exist.
        private readonly HashSet<JsonObject> headerTables = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<JsonObject> dottedTables = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<JsonNode> inlineValues = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<JsonArray> tableArrays = new(ReferenceEqualityComparer.Instance);
        private int position;

        private bool End => position >= text.Length;

        private char Current => text[position];

        public JsonObject Document()
        {
            var root = new JsonObject();
            var table = root;
            while (true)
            {
                SkipBlankLines();
                if (End)
                {
                    return root;
                }

                if (Current == '[')
                {
                    table = Header(root);
                }
                else
                {
                    KeyValue(table, depth: 0);
                }

                ExpectLineEnd();
            }
        }

        private JsonObject Header(JsonObject root)
        {
            var isArray = position + 1 < text.Length && text[position + 1] == '[';
            position += isArray ? 2 : 1;
            SkipWhitespace();
            var keys = Key();
            SkipWhitespace();
            Expect(']');
            if (isArray)
            {
                Expect(']');
            }

            var table = root;
            for (var i = 0; i < keys.Count - 1; i++)
            {
                table = HeaderSegment(table, keys[i]);
            }

            var last = keys[^1];
            table.TryGetPropertyValue(last, out var existing);
            if (isArray)
            {
                if (existing is null && !table.ContainsKey(last))
                {
                    existing = new JsonArray();
                    tableArrays.Add((JsonArray)existing);
                    table[last] = existing;
                }
                else if (existing is not JsonArray array || !tableArrays.Contains(array))
                {
                    throw Error($"'{last}' is already defined and cannot become an array of tables.");
                }

                var element = new JsonObject();
                ((JsonArray)existing).Add((JsonNode)element);
                headerTables.Add(element);
                return element;
            }

            if (!table.ContainsKey(last))
            {
                var created = new JsonObject();
                table[last] = created;
                headerTables.Add(created);
                return created;
            }

            // Only a table that exists implicitly, as part of an earlier header path, may be opened.
            if (existing is JsonObject implicitTable && !headerTables.Contains(implicitTable)
                && !dottedTables.Contains(implicitTable) && !inlineValues.Contains(implicitTable))
            {
                headerTables.Add(implicitTable);
                return implicitTable;
            }

            throw Error(existing is JsonObject
                ? $"Table '{string.Join('.', keys)}' is defined more than once."
                : $"'{string.Join('.', keys)}' is already defined as a value.");
        }

        private JsonObject HeaderSegment(JsonObject table, string key)
        {
            if (!table.TryGetPropertyValue(key, out var node))
            {
                var created = new JsonObject();
                table[key] = created;
                return created;
            }

            return node switch
            {
                JsonObject child when !inlineValues.Contains(child) => child,
                JsonArray array when tableArrays.Contains(array) => (JsonObject)(array[^1] ?? throw Error("Empty array of tables.")),
                _ => throw Error($"'{key}' is already defined as a value."),
            };
        }

        private void KeyValue(JsonObject table, int depth)
        {
            var keys = Key();
            SkipWhitespace();
            Expect('=');
            SkipWhitespace();
            for (var i = 0; i < keys.Count - 1; i++)
            {
                if (!table.TryGetPropertyValue(keys[i], out var node))
                {
                    var created = new JsonObject();
                    table[keys[i]] = created;
                    dottedTables.Add(created);
                    table = created;
                }
                else if (node is JsonObject child && dottedTables.Contains(child))
                {
                    table = child;
                }
                else
                {
                    throw Error($"Dotted key '{string.Join('.', keys)}' redefines '{keys[i]}'.");
                }
            }

            if (table.ContainsKey(keys[^1]))
            {
                throw Error($"Key '{string.Join('.', keys)}' is defined more than once.");
            }

            table[keys[^1]] = Value(depth + 1);
        }

        private List<string> Key()
        {
            var parts = new List<string>();
            while (true)
            {
                if (parts.Count >= MaximumDepth)
                {
                    throw Error("Key exceeds the depth bound.");
                }

                if (End)
                {
                    throw Error("Key expected.");
                }

                if (Current == '"')
                {
                    position++;
                    parts.Add(BasicString());
                }
                else if (Current == '\'')
                {
                    position++;
                    parts.Add(LiteralString());
                }
                else
                {
                    var start = position;
                    while (!End && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-'))
                    {
                        position++;
                    }

                    if (position == start)
                    {
                        throw Error("Key expected.");
                    }

                    parts.Add(text[start..position]);
                }

                SkipWhitespace();
                if (End || Current != '.')
                {
                    return parts;
                }

                position++;
                SkipWhitespace();
            }
        }

        private JsonNode Value(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw Error("Value exceeds the depth bound.");
            }

            if (End)
            {
                throw Error("Value expected.");
            }

            switch (Current)
            {
                case '"' when StartsWith("\"\"\""):
                    position += 3;
                    return JsonValue.Create(MultilineBasicString());
                case '"':
                    position++;
                    return JsonValue.Create(BasicString());
                case '\'' when StartsWith("'''"):
                    position += 3;
                    return JsonValue.Create(MultilineLiteralString());
                case '\'':
                    position++;
                    return JsonValue.Create(LiteralString());
                case '[':
                    return Array(depth);
                case '{':
                    return InlineTable(depth);
            }

            var start = position;
            while (!End && Current is not (' ' or '\t' or ',' or ']' or '}' or '#' or '\n' or '\r'))
            {
                position++;
            }

            var token = text[start..position];
            if (token == "true")
            {
                return JsonValue.Create(true);
            }

            if (token == "false")
            {
                return JsonValue.Create(false);
            }

            if (DecimalInteger().IsMatch(token))
            {
                return long.TryParse(token.Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                    ? JsonValue.Create(integer)
                    : throw Error($"Integer '{token}' is out of range.");
            }

            if (PrefixedInteger().IsMatch(token))
            {
                return JsonValue.Create(Prefixed(token));
            }

            if (Float().IsMatch(token))
            {
                var number = double.Parse(token.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture);
                return double.IsFinite(number) ? JsonValue.Create(number) : throw Error($"Float '{token}' is out of range.");
            }

            throw Error(token.Length == 0
                ? "Value expected."
                : $"Unsupported value '{token}': dates, times, inf and nan have no JSON form.");
        }

        private long Prefixed(string token)
        {
            var radix = token[1] switch { 'x' => 16, 'o' => 8, _ => 2 };
            var digits = token[2..].Replace("_", "", StringComparison.Ordinal);
            ulong value = 0;
            foreach (var digit in digits)
            {
                var number = (ulong)(char.IsAsciiDigit(digit) ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10);
                if (value > (long.MaxValue - number) / (ulong)radix)
                {
                    throw Error($"Integer '{token}' is out of range.");
                }

                value = value * (ulong)radix + number;
            }

            return (long)value;
        }

        private JsonArray Array(int depth)
        {
            position++;
            var array = new JsonArray();
            while (true)
            {
                SkipBlankLines();
                if (End)
                {
                    throw Error("Unterminated array.");
                }

                if (Current == ']')
                {
                    position++;
                    return array;
                }

                array.Add(Value(depth + 1));
                SkipBlankLines();
                if (!End && Current == ',')
                {
                    position++;
                    continue;
                }

                SkipBlankLines();
                Expect(']');
                return array;
            }
        }

        private JsonObject InlineTable(int depth)
        {
            position++;
            var table = new JsonObject();
            while (true)
            {
                SkipBlankLines();
                if (End)
                {
                    throw Error("Unterminated inline table.");
                }

                if (Current == '}')
                {
                    position++;
                    break;
                }

                KeyValue(table, depth);
                SkipBlankLines();
                if (!End && Current == ',')
                {
                    position++;
                    continue;
                }

                SkipBlankLines();
                Expect('}');
                break;
            }

            Freeze(table);
            return table;
        }

        // An inline table is complete: no header or dotted key may add to it or to its sub-tables.
        private void Freeze(JsonNode node)
        {
            inlineValues.Add(node);
            switch (node)
            {
                case JsonObject table:
                    dottedTables.Remove(table);
                    foreach (var item in table)
                    {
                        if (item.Value is not null)
                        {
                            Freeze(item.Value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is not null)
                        {
                            Freeze(item);
                        }
                    }

                    break;
            }
        }

        private string BasicString()
        {
            var builder = new StringBuilder();
            while (true)
            {
                if (End)
                {
                    throw Error("Unterminated string.");
                }

                var c = Current;
                position++;
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    Escape(builder);
                }
                else if (IsForbiddenControl(c) || c is '\n' or '\r')
                {
                    throw Error("Control characters must be escaped in strings.");
                }
                else
                {
                    builder.Append(c);
                }
            }
        }

        private string MultilineBasicString()
        {
            SkipFirstNewline();
            var builder = new StringBuilder();
            while (true)
            {
                if (End)
                {
                    throw Error("Unterminated multi-line string.");
                }

                if (StartsWith("\"\"\""))
                {
                    // Up to two quotes may sit directly before the closing delimiter.
                    var quotes = 3;
                    while (quotes < 5 && position + quotes < text.Length && text[position + quotes] == '"')
                    {
                        quotes++;
                    }

                    builder.Append('"', quotes - 3);
                    position += quotes;
                    return builder.ToString();
                }

                var c = Current;
                position++;
                if (c == '\\')
                {
                    if (LineEndingBackslash())
                    {
                        continue;
                    }

                    Escape(builder);
                }
                else if (c == '\r')
                {
                    if (End || Current != '\n')
                    {
                        throw Error("Bare carriage return in string.");
                    }

                    position++;
                    builder.Append("\r\n");
                }
                else if (IsForbiddenControl(c))
                {
                    throw Error("Control characters must be escaped in strings.");
                }
                else
                {
                    builder.Append(c);
                }
            }
        }

        // A backslash followed only by whitespace up to the end of the line trims all following
        // whitespace and newlines.
        private bool LineEndingBackslash()
        {
            var scan = position;
            while (scan < text.Length && text[scan] is ' ' or '\t')
            {
                scan++;
            }

            if (scan >= text.Length || (text[scan] != '\n' && !(text[scan] == '\r' && scan + 1 < text.Length && text[scan + 1] == '\n')))
            {
                return false;
            }

            position = scan;
            while (!End && Current is ' ' or '\t' or '\n' or '\r')
            {
                if (Current == '\r' && (position + 1 >= text.Length || text[position + 1] != '\n'))
                {
                    throw Error("Bare carriage return in string.");
                }

                position++;
            }

            return true;
        }

        private string LiteralString()
        {
            var start = position;
            while (true)
            {
                if (End)
                {
                    throw Error("Unterminated string.");
                }

                var c = Current;
                if (c == '\'')
                {
                    var value = text[start..position];
                    position++;
                    return value;
                }

                if (IsForbiddenControl(c) || c is '\n' or '\r')
                {
                    throw Error("Control characters are not allowed in literal strings.");
                }

                position++;
            }
        }

        private string MultilineLiteralString()
        {
            SkipFirstNewline();
            var builder = new StringBuilder();
            while (true)
            {
                if (End)
                {
                    throw Error("Unterminated multi-line string.");
                }

                if (StartsWith("'''"))
                {
                    var quotes = 3;
                    while (quotes < 5 && position + quotes < text.Length && text[position + quotes] == '\'')
                    {
                        quotes++;
                    }

                    builder.Append('\'', quotes - 3);
                    position += quotes;
                    return builder.ToString();
                }

                var c = Current;
                position++;
                if (c == '\r')
                {
                    if (End || Current != '\n')
                    {
                        throw Error("Bare carriage return in string.");
                    }

                    position++;
                    builder.Append("\r\n");
                }
                else if (IsForbiddenControl(c))
                {
                    throw Error("Control characters are not allowed in literal strings.");
                }
                else
                {
                    builder.Append(c);
                }
            }
        }

        private void Escape(StringBuilder builder)
        {
            if (End)
            {
                throw Error("Unterminated escape sequence.");
            }

            var c = Current;
            position++;
            switch (c)
            {
                case 'b': builder.Append('\b'); break;
                case 't': builder.Append('\t'); break;
                case 'n': builder.Append('\n'); break;
                case 'f': builder.Append('\f'); break;
                case 'r': builder.Append('\r'); break;
                case 'e': builder.Append('\u001B'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case 'x': builder.Append(CodePoint(2)); break;
                case 'u': builder.Append(CodePoint(4)); break;
                case 'U': builder.Append(CodePoint(8)); break;
                default: throw Error($"Invalid escape sequence '\\{c}'.");
            }
        }

        private string CodePoint(int digits)
        {
            if (position + digits > text.Length
                || !int.TryParse(text.AsSpan(position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
                || value is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
            {
                throw Error("Invalid Unicode escape.");
            }

            position += digits;
            return char.ConvertFromUtf32(value);
        }

        private void SkipFirstNewline()
        {
            if (StartsWith("\n"))
            {
                position++;
            }
            else if (StartsWith("\r\n"))
            {
                position += 2;
            }
        }

        private void SkipWhitespace()
        {
            while (!End && Current is ' ' or '\t')
            {
                position++;
            }
        }

        private void SkipComment()
        {
            if (End || Current != '#')
            {
                return;
            }

            while (!End && Current is not ('\n' or '\r'))
            {
                if (IsForbiddenControl(Current))
                {
                    throw Error("Control characters are not allowed in comments.");
                }

                position++;
            }
        }

        private bool SkipNewline()
        {
            if (StartsWith("\n"))
            {
                position++;
                return true;
            }

            if (StartsWith("\r\n"))
            {
                position += 2;
                return true;
            }

            return false;
        }

        private void SkipBlankLines()
        {
            while (true)
            {
                SkipWhitespace();
                SkipComment();
                if (!SkipNewline())
                {
                    return;
                }
            }
        }

        private void ExpectLineEnd()
        {
            SkipWhitespace();
            SkipComment();
            if (!End && !SkipNewline())
            {
                throw Error("Expected the end of the line.");
            }
        }

        private void Expect(char expected)
        {
            if (End || Current != expected)
            {
                throw Error($"Expected '{expected}'.");
            }

            position++;
        }

        private bool StartsWith(string value) => text.AsSpan(position).StartsWith(value, StringComparison.Ordinal);

        private static bool IsForbiddenControl(char c) => (c < 0x20 && c is not ('\t' or '\n' or '\r')) || c == '\u007F';

        private FormatException Error(string message)
        {
            var line = 1;
            for (var i = 0; i < Math.Min(position, text.Length); i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return new FormatException($"TOML line {line}: {message}");
        }
    }
}
