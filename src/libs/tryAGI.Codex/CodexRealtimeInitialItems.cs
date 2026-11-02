using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Immutable, value-comparable settings collection; identical imports do not restart live voice.</summary>
[JsonConverter(typeof(CodexRealtimeInitialItems.Converter))]
internal sealed class CodexRealtimeInitialItems : IReadOnlyList<CodexRealtimeInitialItem>, IEquatable<CodexRealtimeInitialItems>
{
    private readonly CodexRealtimeInitialItem[] items;
    public CodexRealtimeInitialItems(IEnumerable<CodexRealtimeInitialItem> items)
    {
        this.items = items.ToArray();
        CodexRealtimeStartupContext.ValidateInitialItems(this.items);
    }
    public int Count => items.Length;
    public CodexRealtimeInitialItem this[int index] => items[index];
    public IEnumerator<CodexRealtimeInitialItem> GetEnumerator() => ((IEnumerable<CodexRealtimeInitialItem>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(CodexRealtimeInitialItems? other) => other is not null && items.SequenceEqual(other.items);
    public override bool Equals(object? obj) => obj is CodexRealtimeInitialItems other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var item in items) hash.Add(item); return hash.ToHashCode(); }
    public sealed class Converter : JsonConverter<CodexRealtimeInitialItems>
    {
        public override CodexRealtimeInitialItems Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("initialItems must be an array.");
            var roleName = options.PropertyNamingPolicy?.ConvertName(nameof(CodexRealtimeInitialItem.Role)) ?? nameof(CodexRealtimeInitialItem.Role);
            var textName = options.PropertyNamingPolicy?.ConvertName(nameof(CodexRealtimeInitialItem.Text)) ?? nameof(CodexRealtimeInitialItem.Text);
            var result = new List<CodexRealtimeInitialItem>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("initialItems entries must be objects.");
                string? role = null; string? text = null; var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String) throw new JsonException("Invalid initial message property.");
                    if (property.Name == roleName) role = property.Value.GetString();
                    else if (property.Name == textName) text = property.Value.GetString();
                    else throw new JsonException("Unknown initial message property.");
                }
                if (role is null || text is null) throw new JsonException("Initial messages require role and text.");
                result.Add(new(role, text));
                if (result.Count > CodexRealtimeStartupContext.MaximumInitialItems) throw new JsonException("Too many initial messages.");
            }
            return new(result);
        }
        public override void Write(Utf8JsonWriter writer, CodexRealtimeInitialItems value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value.items)
            {
                writer.WriteStartObject();
                writer.WriteString(options.PropertyNamingPolicy?.ConvertName(nameof(CodexRealtimeInitialItem.Role)) ?? nameof(CodexRealtimeInitialItem.Role), item.Role);
                writer.WriteString(options.PropertyNamingPolicy?.ConvertName(nameof(CodexRealtimeInitialItem.Text)) ?? nameof(CodexRealtimeInitialItem.Text), item.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }
}
