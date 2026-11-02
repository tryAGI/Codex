using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class UserInputTests
{
    [Fact]
    public void KnownTypesMatchTheNewestSchemaUnion()
    {
        // A variant added or removed upstream fails the drift pull request instead of merging it.
        var specs = Path.Combine(AppContext.BaseDirectory, "specs", CodexAppServerProtocol.Version, "codex_app_server_protocol.schemas.json");
        using var schema = JsonDocument.Parse(File.ReadAllBytes(specs));
        var union = schema.RootElement.GetProperty("definitions").GetProperty("v2").GetProperty("UserInput").GetProperty("oneOf");
        var types = union.EnumerateArray()
            .Select(variant => variant.GetProperty("properties").GetProperty("type").GetProperty("enum")[0].GetString())
            .OfType<string>()
            .Order(StringComparer.Ordinal);

        Assert.Equal(types, CodexUserInput.KnownTypes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{"type":"text","text":"hello"}""")]
    [InlineData("""{"type":"text","text":"see @a","text_elements":[{"byteRange":{"start":4,"end":6},"placeholder":"a.cs"}]}""")]
    [InlineData("""{"type":"image","url":"https://example.com/a.png","detail":"high"}""")]
    [InlineData("""{"type":"image","fileId":"file_1"}""")]
    [InlineData("""{"type":"localImage","path":"/tmp/a.png","detail":"original"}""")]
    [InlineData("""{"type":"audio","url":"https://example.com/a.wav"}""")]
    [InlineData("""{"type":"localAudio","path":"/tmp/a.wav"}""")]
    [InlineData("""{"type":"skill","name":"review","path":"/skills/review"}""")]
    [InlineData("""{"type":"mention","name":"a.cs","path":"/src/a.cs"}""")]
    [InlineData("""{"type":"futureVariant","value":1}""")]
    public void RoundTripsEveryVariantWithoutAddingMembers(string json)
    {
        using var document = JsonDocument.Parse(json);

        var input = CodexUserInput.Parse(document.RootElement);

        Assert.True(JsonElement.DeepEquals(document.RootElement, JsonDocument.Parse(input.ToJson().ToJsonString()).RootElement));
    }

    [Fact]
    public void UnknownVariantIsPreserved()
    {
        using var document = JsonDocument.Parse("""{"type":"futureVariant","value":1}""");

        var input = Assert.IsType<CodexUnknownInput>(CodexUserInput.Parse(document.RootElement));

        Assert.Equal("futureVariant", input.Type);
    }

    [Fact]
    public void OptionalMembersAreOmittedRatherThanWrittenAsNull()
    {
        var json = CodexUserInput.ToJsonArray([
            new CodexTextInput("hi"),
            CodexImageInput.FromUrl("https://example.com/a.png"),
            new CodexLocalImageInput("/tmp/a.png"),
        ]).ToJsonString();

        Assert.Equal("""[{"type":"text","text":"hi"},{"type":"image","url":"https://example.com/a.png"},{"type":"localImage","path":"/tmp/a.png"}]""", json);
    }

    [Fact]
    public void FromBytesBuildsADataUrl()
    {
        var image = CodexImageInput.FromBytes([1, 2, 3], "image/png", CodexImageDetail.Low);
        var audio = CodexAudioInput.FromBytes([4, 5], "audio/wav");

        Assert.Equal("data:image/png;base64,AQID", image.Url);
        Assert.Equal(CodexImageDetail.Low, image.Detail);
        Assert.Equal("data:audio/wav;base64,BAU=", audio.Url);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/png;charset=x")]
    [InlineData("")]
    public void FromBytesRejectsAMediaTypeOfTheWrongKind(string mediaType)
    {
        Assert.Throws<ArgumentException>(() => CodexImageInput.FromBytes([1], mediaType));
    }

    [Theory]
    [InlineData("""{"type":"image"}""")]
    [InlineData("""{"type":"image","url":"https://example.com/a.png","fileId":"file_1"}""")]
    [InlineData("""{"type":"image","url":"https://example.com/a.png","detail":"huge"}""")]
    [InlineData("""{"type":"text"}""")]
    [InlineData("""{"type":"text","text":5}""")]
    [InlineData("""{"type":"text","text":"x","text_elements":[{"byteRange":{"start":3,"end":1}}]}""")]
    [InlineData("""{"text":"no type"}""")]
    [InlineData("""[]""")]
    public void RejectsMalformedInput(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Throws<FormatException>(() => CodexUserInput.Parse(document.RootElement));
    }

    [Fact]
    public void ParseArrayKeepsOrder()
    {
        using var document = JsonDocument.Parse("""[{"type":"text","text":"a"},{"type":"mention","name":"b","path":"/b"}]""");

        var inputs = CodexUserInput.ParseArray(document.RootElement);

        Assert.Collection(inputs,
            first => Assert.Equal("a", Assert.IsType<CodexTextInput>(first).Text),
            second => Assert.Equal("/b", Assert.IsType<CodexMentionInput>(second).Path));
    }
}
