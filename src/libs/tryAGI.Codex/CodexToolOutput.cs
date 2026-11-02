using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host-authorized pixels. The SDK never fetches an arbitrary image URL.</summary>
/// <param name="Data">Image bytes.</param>
/// <param name="MimeType"><c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c> or <c>image/gif</c>.</param>
public sealed record CodexToolImage(ReadOnlyMemory<byte> Data, string MimeType);

/// <summary>Text and optional image contents belonging to one exact function/custom call.</summary>
/// <param name="Text">Text output.</param>
public sealed record CodexToolOutput(string Text)
{
    /// <summary>Images returned with the text.</summary>
    public IReadOnlyList<CodexToolImage> Images { get; init; } = [];
    /// <summary>Limits the images must fit.</summary>
    public CodexToolImageLimits ImageLimits { get; init; } = new();

    internal JsonNode Content()
    {
        ArgumentNullException.ThrowIfNull(Text);
        ArgumentNullException.ThrowIfNull(Images);
        ArgumentNullException.ThrowIfNull(ImageLimits);
        ImageLimits.Validate();
        if (Images.Count == 0) return (JsonValue.Create(Text) ?? throw new InvalidDataException("Required data was null."));
        if (Images.Count > ImageLimits.MaximumImages) throw new ArgumentException("Tool output exceeds its host-authorized image count.");
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = Text });
        var bytes = 0;
        foreach (var image in Images)
        {
            if (image.MimeType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif") ||
                image.Data.IsEmpty || image.Data.Length > ImageLimits.MaximumTotalBytes - bytes)
                throw new ArgumentException("Tool images require a supported MIME type and must fit their host-authorized total byte budget.");
            bytes += image.Data.Length;
            content.Add((JsonNode)new JsonObject { ["type"] = "input_image",
                ["image_url"] = "data:" + image.MimeType + ";base64," + Convert.ToBase64String(image.Data.Span) });
        }
        return content;
    }
}
