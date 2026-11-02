using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>How much detail the model should use for an image input.</summary>
public enum CodexImageDetail
{
    /// <summary>Let the server choose (<c>auto</c>).</summary>
    Auto,

    /// <summary>Low detail (<c>low</c>).</summary>
    Low,

    /// <summary>High detail (<c>high</c>).</summary>
    High,

    /// <summary>The image at its original resolution (<c>original</c>).</summary>
    Original,
}

/// <summary>A byte range inside the parent text of a <see cref="CodexTextInput"/>.</summary>
/// <param name="Start">First byte, inclusive.</param>
/// <param name="End">Last byte, exclusive.</param>
public sealed record CodexByteRange(long Start, long End);

/// <summary>A UI-defined span within <see cref="CodexTextInput.Text"/>, such as a rendered mention.</summary>
/// <param name="ByteRange">Where the element sits in the UTF-8 encoded text.</param>
/// <param name="Placeholder">Optional human-readable placeholder shown in the UI.</param>
public sealed record CodexTextElement(CodexByteRange ByteRange, string? Placeholder = null);

/// <summary>
/// One element of the App Server <c>UserInput</c> union, as sent in <c>turn/start</c> and
/// <c>turn/steer</c>. Each variant writes only the members it declares, so no <c>null</c>
/// members reach the server.
/// </summary>
/// <remarks>
/// Use <see cref="ToJson()"/> or <see cref="ToJsonArray"/> to build request parameters and
/// <see cref="Parse"/> to read them. Variants this version does not know are preserved as
/// <see cref="CodexUnknownInput"/>.
/// </remarks>
public abstract record CodexUserInput
{
    private protected CodexUserInput()
    {
    }

    /// <summary>The wire discriminator (<c>text</c>, <c>image</c>, <c>localImage</c>, …).</summary>
    public abstract string Type { get; }

    /// <summary>The discriminators of every variant this version models.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } =
        ["text", "image", "localImage", "audio", "localAudio", "skill", "mention"];

    /// <summary>Serializes this input to its wire form.</summary>
    /// <returns>A new JSON object.</returns>
    public abstract JsonObject ToJson();

    /// <summary>Serializes a list of inputs to the <c>input</c> array of <c>turn/start</c>.</summary>
    /// <param name="inputs">Inputs in order.</param>
    /// <returns>A new JSON array.</returns>
    public static JsonArray ToJsonArray(IEnumerable<CodexUserInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var array = new JsonArray();
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            array.Add((JsonNode)input.ToJson());
        }

        return array;
    }

    /// <summary>Reads one wire element.</summary>
    /// <param name="element">A JSON object with a <c>type</c> member.</param>
    /// <returns>The typed variant, or <see cref="CodexUnknownInput"/> for a type this version does not model.</returns>
    /// <exception cref="FormatException">The element is not an object, lacks <c>type</c>, or a required member is missing or has the wrong type.</exception>
    public static CodexUserInput Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("User input must be a JSON object.");
        }

        var type = OptionalString(element, "type") ?? throw new FormatException("User input requires a string 'type'.");
        return type switch
        {
            "text" => new CodexTextInput(RequiredString(element, "text"), TextElements(element)),
            "image" => Image(element),
            "localImage" => new CodexLocalImageInput(RequiredString(element, "path"), Detail(element)),
            "audio" => new CodexAudioInput(RequiredString(element, "url")),
            "localAudio" => new CodexLocalAudioInput(RequiredString(element, "path")),
            "skill" => new CodexSkillInput(RequiredString(element, "name"), RequiredString(element, "path")),
            "mention" => new CodexMentionInput(RequiredString(element, "name"), RequiredString(element, "path")),
            _ => new CodexUnknownInput(element.Clone()),
        };
    }

    /// <summary>Reads the <c>input</c> array of a <c>turn/start</c> request.</summary>
    /// <param name="array">A JSON array of user inputs.</param>
    /// <returns>The typed inputs in order.</returns>
    /// <exception cref="FormatException">The value is not an array, or an element is malformed.</exception>
    public static IReadOnlyList<CodexUserInput> ParseArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("User input list must be a JSON array.");
        }

        var inputs = new List<CodexUserInput>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            inputs.Add(Parse(element));
        }

        return inputs;
    }

    private protected static JsonObject Object(string type) => new() { ["type"] = type };

    private protected static string Wire(CodexImageDetail detail) => detail switch
    {
        CodexImageDetail.Auto => "auto",
        CodexImageDetail.Low => "low",
        CodexImageDetail.High => "high",
        CodexImageDetail.Original => "original",
        _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "Unknown image detail."),
    };

    private protected static string Required(string value, string name) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"'{name}' is required.", name) : value;

    private protected static string DataUrl(ReadOnlySpan<byte> data, string mediaType, string kind, string parameter)
    {
        if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith(kind + "/", StringComparison.Ordinal)
            || mediaType.IndexOfAny([';', ',', ' ', '\t', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"A single '{kind}/…' media type is required.", parameter);
        }

        if (data.IsEmpty)
        {
            throw new ArgumentException("Data must not be empty.", nameof(data));
        }

        return $"data:{mediaType};base64,{Convert.ToBase64String(data)}";
    }

    private static CodexImageInput Image(JsonElement element)
    {
        var url = OptionalString(element, "url");
        var fileId = OptionalString(element, "fileId");
        var detail = Detail(element);
        return (url, fileId) switch
        {
            ({ } u, null) => CodexImageInput.FromUrl(u, detail),
            (null, { } f) => CodexImageInput.FromFileId(f, detail),
            _ => throw new FormatException("Image input requires exactly one of 'url' or 'fileId'."),
        };
    }

    private static CodexImageDetail? Detail(JsonElement element) => OptionalString(element, "detail") switch
    {
        null => null,
        "auto" => CodexImageDetail.Auto,
        "low" => CodexImageDetail.Low,
        "high" => CodexImageDetail.High,
        "original" => CodexImageDetail.Original,
        var other => throw new FormatException($"Unknown image detail '{other}'."),
    };

    private static IReadOnlyList<CodexTextElement> TextElements(JsonElement element)
    {
        if (!element.TryGetProperty("text_elements", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("'text_elements' must be an array.");
        }

        var elements = new List<CodexTextElement>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("byteRange", out var range)
                || range.ValueKind != JsonValueKind.Object
                || !range.TryGetProperty("start", out var start) || !start.TryGetInt64(out var first) || first < 0
                || !range.TryGetProperty("end", out var end) || !end.TryGetInt64(out var last) || last < first)
            {
                throw new FormatException("Each text element requires a valid 'byteRange'.");
            }

            elements.Add(new CodexTextElement(new CodexByteRange(first, last), OptionalString(item, "placeholder")));
        }

        return elements;
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new FormatException($"User input requires a string '{name}'.");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new FormatException($"'{name}' must be a string.");
    }
}

/// <summary>Text typed by the user (<c>text</c>).</summary>
/// <param name="Text">The text.</param>
/// <param name="TextElements">UI-defined spans within the text. Omitted from the wire form when empty.</param>
public sealed record CodexTextInput(string Text, IReadOnlyList<CodexTextElement>? TextElements = null) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "text";

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["text"] = Text ?? throw new InvalidOperationException("Text is required.");
        if (TextElements is { Count: > 0 } elements)
        {
            var array = new JsonArray();
            foreach (var element in elements)
            {
                var item = new JsonObject
                {
                    ["byteRange"] = new JsonObject { ["start"] = element.ByteRange.Start, ["end"] = element.ByteRange.End },
                };
                if (element.Placeholder is { } placeholder)
                {
                    item["placeholder"] = placeholder;
                }

                array.Add((JsonNode)item);
            }

            json["text_elements"] = array;
        }

        return json;
    }
}

/// <summary>An image referenced by URL or uploaded file id (<c>image</c>). Create with
/// <see cref="FromUrl"/>, <see cref="FromBytes"/> or <see cref="FromFileId"/>.</summary>
public sealed record CodexImageInput : CodexUserInput
{
    private CodexImageInput(string? url, string? fileId, CodexImageDetail? detail)
    {
        Url = url;
        FileId = fileId;
        Detail = detail;
    }

    /// <inheritdoc/>
    public override string Type => "image";

    /// <summary>An <c>https</c> or <c>data:image/…</c> URL, when the image is given by URL.</summary>
    public string? Url { get; }

    /// <summary>An uploaded file id, when the image is given by file.</summary>
    public string? FileId { get; }

    /// <summary>Requested detail level; the server's default when <see langword="null"/>.</summary>
    public CodexImageDetail? Detail { get; }

    /// <summary>An image at a URL.</summary>
    /// <param name="url">An <c>https</c> or <c>data:image/…</c> URL.</param>
    /// <param name="detail">Requested detail level.</param>
    /// <returns>The input.</returns>
    public static CodexImageInput FromUrl(string url, CodexImageDetail? detail = null) => new(Required(url, nameof(url)), null, detail);

    /// <summary>An image the caller holds in memory, sent inline as a <c>data:</c> URL.</summary>
    /// <param name="data">Encoded image bytes.</param>
    /// <param name="mediaType">Media type such as <c>image/png</c>.</param>
    /// <param name="detail">Requested detail level.</param>
    /// <returns>The input.</returns>
    public static CodexImageInput FromBytes(ReadOnlySpan<byte> data, string mediaType, CodexImageDetail? detail = null) =>
        new(DataUrl(data, mediaType, "image", nameof(mediaType)), null, detail);

    /// <summary>An image previously uploaded as a file.</summary>
    /// <param name="fileId">The file id.</param>
    /// <param name="detail">Requested detail level.</param>
    /// <returns>The input.</returns>
    public static CodexImageInput FromFileId(string fileId, CodexImageDetail? detail = null) => new(null, Required(fileId, nameof(fileId)), detail);

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        if (Url is { } url)
        {
            json["url"] = url;
        }
        else
        {
            json["fileId"] = FileId ?? throw new InvalidOperationException("An image input needs a URL or a file id.");
        }

        if (Detail is { } detail)
        {
            json["detail"] = Wire(detail);
        }

        return json;
    }
}

/// <summary>An image file on the machine running the App Server (<c>localImage</c>).</summary>
/// <param name="Path">Path on the server's machine.</param>
/// <param name="Detail">Requested detail level; the server's default when <see langword="null"/>.</param>
public sealed record CodexLocalImageInput(string Path, CodexImageDetail? Detail = null) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "localImage";

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["path"] = Required(Path, nameof(Path));
        if (Detail is { } detail)
        {
            json["detail"] = Wire(detail);
        }

        return json;
    }
}

/// <summary>Audio referenced by URL (<c>audio</c>).</summary>
/// <param name="Url">An <c>https</c> or <c>data:audio/…</c> URL.</param>
public sealed record CodexAudioInput(string Url) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "audio";

    /// <summary>Audio the caller holds in memory, sent inline as a <c>data:</c> URL.</summary>
    /// <param name="data">Encoded audio bytes.</param>
    /// <param name="mediaType">Media type such as <c>audio/wav</c>.</param>
    /// <returns>The input.</returns>
    public static CodexAudioInput FromBytes(ReadOnlySpan<byte> data, string mediaType) =>
        new(DataUrl(data, mediaType, "audio", nameof(mediaType)));

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["url"] = Required(Url, nameof(Url));
        return json;
    }
}

/// <summary>An audio file on the machine running the App Server (<c>localAudio</c>).</summary>
/// <param name="Path">Path on the server's machine.</param>
public sealed record CodexLocalAudioInput(string Path) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "localAudio";

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["path"] = Required(Path, nameof(Path));
        return json;
    }
}

/// <summary>A skill to load for the turn (<c>skill</c>).</summary>
/// <param name="Name">Skill name.</param>
/// <param name="Path">Path of the skill on the server's machine.</param>
public sealed record CodexSkillInput(string Name, string Path) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "skill";

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["name"] = Required(Name, nameof(Name));
        json["path"] = Required(Path, nameof(Path));
        return json;
    }
}

/// <summary>A mentioned file or resource (<c>mention</c>).</summary>
/// <param name="Name">Display name.</param>
/// <param name="Path">Path of the mentioned resource.</param>
public sealed record CodexMentionInput(string Name, string Path) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => "mention";

    /// <inheritdoc/>
    public override JsonObject ToJson()
    {
        var json = Object(Type);
        json["name"] = Required(Name, nameof(Name));
        json["path"] = Required(Path, nameof(Path));
        return json;
    }
}

/// <summary>A variant this version does not model, kept verbatim so it can be forwarded unchanged.</summary>
/// <param name="Raw">The original JSON object.</param>
public sealed record CodexUnknownInput(JsonElement Raw) : CodexUserInput
{
    /// <inheritdoc/>
    public override string Type => Raw.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
        ? type.GetString() ?? string.Empty
        : string.Empty;

    /// <inheritdoc/>
    public override JsonObject ToJson() =>
        JsonNode.Parse(Raw.GetRawText()) as JsonObject ?? throw new InvalidOperationException("Raw input is not a JSON object.");
}
