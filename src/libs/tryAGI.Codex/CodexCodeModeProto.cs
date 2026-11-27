using System.Buffers;
using System.Text;

namespace tryAGI.Codex;

// Bounded protobuf codec for the pinned proto3 subset, avoiding reflection and generated
// dependencies. Unsupported wire types and ambiguous scalar/oneof members fail closed.
internal sealed class CodexCodeModeProtoWriter
{
    private readonly ArrayBufferWriter<byte> buffer = new();

    internal void UInt(int field, ulong value)
    {
        Varint((ulong)field << 3);
        Varint(value);
    }

    internal void Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    internal void Bytes(int field, ReadOnlySpan<byte> value)
    {
        Varint(((ulong)field << 3) | 2);
        Varint((ulong)value.Length);
        buffer.Write(value);
    }

    internal void Message(int field, Action<CodexCodeModeProtoWriter> build)
    {
        var nested = new CodexCodeModeProtoWriter();
        build(nested);
        Bytes(field, nested.ToArray());
    }

    internal byte[] ToArray() => buffer.WrittenSpan.ToArray();

    private void Varint(ulong value)
    {
        while (value >= 128)
        {
            buffer.GetSpan(1)[0] = (byte)(value | 0x80);
            buffer.Advance(1);
            value >>= 7;
        }
        buffer.GetSpan(1)[0] = (byte)value;
        buffer.Advance(1);
    }
}

internal sealed class CodexCodeModeProto
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<int, List<Field>> fields = [];

    internal CodexCodeModeProto(ReadOnlyMemory<byte> bytes)
    {
        var offset = 0;
        var span = bytes.Span;
        var count = 0;
        while (offset < span.Length)
        {
            if (++count > 65536) throw new CodexProtocolException("Code Mode protobuf field limit exceeded.");
            var tag = Varint(span, ref offset);
            var number = tag >> 3;
            var wire = (int)(tag & 7);
            if (number is 0 or > 536870911) throw new CodexProtocolException("Invalid Code Mode protobuf tag.");
            Field field;
            switch (wire)
            {
                case 0:
                    field = new(wire, Varint(span, ref offset), default);
                    break;
                case 1:
                case 5:
                    var fixedLength = wire == 1 ? 8 : 4;
                    if (span.Length - offset < fixedLength) throw Truncated();
                    field = new(wire, 0, bytes.Slice(offset, fixedLength));
                    offset += fixedLength;
                    break;
                case 2:
                    var length = Varint(span, ref offset);
                    if (length > (ulong)(span.Length - offset)) throw Truncated();
                    field = new(wire, 0, bytes.Slice(offset, (int)length));
                    offset += (int)length;
                    break;
                default:
                    throw new CodexProtocolException("Unsupported Code Mode protobuf wire type.");
            }
            var key = (int)number;
            if (!fields.TryGetValue(key, out var list)) fields.Add(key, list = []);
            list.Add(field);
        }
    }

    internal bool Has(int field) => fields.ContainsKey(field);

    internal ulong UInt(int field, ulong fallback = 0) => One(field, 0)?.Number ?? fallback;

    internal bool Boolean(int field) => UInt(field) switch
    {
        0 => false,
        1 => true,
        _ => throw new CodexProtocolException("Invalid Code Mode protobuf boolean."),
    };

    internal ReadOnlyMemory<byte>? Bytes(int field) => One(field, 2)?.Bytes;

    internal string? Text(int field) => Bytes(field) is { } data ? StrictUtf8.GetString(data.Span) : null;

    internal string RequiredText(int field)
    {
        var text = Text(field);
        if (string.IsNullOrWhiteSpace(text)) throw new CodexProtocolException("Missing Code Mode protobuf identity.");
        return text;
    }

    internal CodexCodeModeProto Message(int field) =>
        new(Bytes(field) ?? throw new CodexProtocolException("Missing Code Mode protobuf message."));

    internal IEnumerable<CodexCodeModeProto> Repeated(int field)
    {
        if (!fields.TryGetValue(field, out var list)) yield break;
        foreach (var item in list)
        {
            if (item.Wire != 2) throw new CodexProtocolException("Invalid Code Mode protobuf repeated message.");
            yield return new(item.Bytes);
        }
    }

    internal int OneOf(params int[] alternatives)
    {
        var found = 0;
        foreach (var field in alternatives)
        {
            if (!Has(field)) continue;
            if (found != 0) throw new CodexProtocolException("Ambiguous Code Mode protobuf oneof.");
            _ = One(field, 2);
            found = field;
        }
        return found != 0 ? found : throw new CodexProtocolException("Missing Code Mode protobuf oneof.");
    }

    private Field? One(int field, int wire)
    {
        if (!fields.TryGetValue(field, out var list)) return null;
        if (list.Count != 1 || list[0].Wire != wire)
            throw new CodexProtocolException("Ambiguous or invalid Code Mode protobuf field.");
        return list[0];
    }

    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == bytes.Length) throw Truncated();
            var next = bytes[offset++];
            if (shift == 63 && next > 1) throw new CodexProtocolException("Code Mode protobuf varint overflow.");
            result |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return result;
        }
        throw new CodexProtocolException("Invalid Code Mode protobuf varint.");
    }

    private static CodexProtocolException Truncated() => new("Truncated Code Mode protobuf message.");

    private readonly record struct Field(int Wire, ulong Number, ReadOnlyMemory<byte> Bytes);
}
