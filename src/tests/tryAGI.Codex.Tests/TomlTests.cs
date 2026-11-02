using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class TomlTests
{
    [Theory]
    // Keys
    [InlineData("a = 1", """{"a":1}""")]
    [InlineData("bare_key-1 = true\n1234 = false", """{"bare_key-1":true,"1234":false}""")]
    [InlineData("\"quoted key\" = 1\n'literal key' = 2\n\"\" = 3", """{"quoted key":1,"literal key":2,"":3}""")]
    [InlineData("a.b.c = 1\na . \"d\" . 'e' = 2", """{"a":{"b":{"c":1},"d":{"e":2}}}""")]
    [InlineData("a.b = 1\na.c = 2", """{"a":{"b":1,"c":2}}""")]
    [InlineData("\"a.b\" = 1", """{"a.b":1}""")]
    // Strings
    [InlineData("s = \"tab\\tquote\\\"slash\\\\ \\u00E9 \\U0001F600 \\e\\x41\"", "{\"s\":\"tab\\tquote\\\"slash\\\\ \u00e9 \U0001F600 \\u001BA\"}")]
    [InlineData("s = 'C:\\Users\\nodejs'", """{"s":"C:\\Users\\nodejs"}""")]
    [InlineData("s = \"\"\"\nline 1\nline 2\"\"\"", """{"s":"line 1\nline 2"}""")]
    [InlineData("s = \"\"\"\nThe quick \\\n\n   brown fox\"\"\"", """{"s":"The quick brown fox"}""")]
    [InlineData("s = \"\"\"Here are two quotes: \"\".\"\"\"", """{"s":"Here are two quotes: \"\"."}""")]
    [InlineData("s = \"\"\"ends in a quote\"\"\"\"", """{"s":"ends in a quote\""}""")]
    [InlineData("s = '''\nraw \\n text\n'''", """{"s":"raw \\n text\n"}""")]
    [InlineData("s = '''it's'''''", """{"s":"it's''"}""")]
    [InlineData("s = \"\"\"crlf\r\nline\"\"\"", """{"s":"crlf\r\nline"}""")]
    [InlineData("s = '''crlf\r\nline'''", """{"s":"crlf\r\nline"}""")]
    [InlineData("s = \"\"\"\\\r\n  trimmed \\\r\n  crlf\"\"\"", """{"s":"trimmed crlf"}""")]
    // Numbers and booleans
    [InlineData("i = +99\nj = -17\nk = 0\nl = 1_000_000", """{"i":99,"j":-17,"k":0,"l":1000000}""")]
    [InlineData("h = 0xDEAD_beef\no = 0o755\nb = 0b1101", """{"h":3735928559,"o":493,"b":13}""")]
    [InlineData("max = 9223372036854775807\nmin = -9223372036854775808", """{"max":9223372036854775807,"min":-9223372036854775808}""")]
    [InlineData("f = 3.14\ng = -0.01\nh = 5e+22\ni = 6.626e-34\nj = 1_000.5", """{"f":3.14,"g":-0.01,"h":5E+22,"i":6.626E-34,"j":1000.5}""")]
    [InlineData("t = true\nf = false", """{"t":true,"f":false}""")]
    // Arrays and inline tables
    [InlineData("a = [ 1, 2, 3 ]\nb = [\"x\", 'y']\nc = [[1, 2], [\"a\"]]", """{"a":[1,2,3],"b":["x","y"],"c":[[1,2],["a"]]}""")]
    [InlineData("a = [\n  1, # one\n  2,\n]\nempty = []", """{"a":[1,2],"empty":[]}""")]
    [InlineData("p = { x = 1, y.z = 2, name = \"n\" }", """{"p":{"x":1,"y":{"z":2},"name":"n"}}""")]
    [InlineData("p = {}\nq = [{ a = 1 }, { a = 2 }]", """{"p":{},"q":[{"a":1},{"a":2}]}""")]
    [InlineData("p = {\n  a = 1, # newline and trailing comma, as in TOML 1.1\n  b = 2,\n}", """{"p":{"a":1,"b":2}}""")]
    // Tables
    [InlineData("[a]\nx = 1\n[b.c]\ny = 2", """{"a":{"x":1},"b":{"c":{"y":2}}}""")]
    [InlineData("[ a . 'b' . \"c\" ]\nx = 1", """{"a":{"b":{"c":{"x":1}}}}""")]
    [InlineData("[x.y.z]\na = 1\n[x]\nb = 2", """{"x":{"y":{"z":{"a":1}},"b":2}}""")]
    [InlineData("[fruit]\napple.color = \"red\"\n[fruit.apple.texture]\nsmooth = true", """{"fruit":{"apple":{"color":"red","texture":{"smooth":true}}}}""")]
    [InlineData("[[p]]\nn = 1\n[[p]]\n[[p]]\nn = 3", """{"p":[{"n":1},{},{"n":3}]}""")]
    [InlineData("[[f]]\nname = \"apple\"\n[f.physical]\ncolor = \"red\"\n[[f.variety]]\nname = \"a\"\n[[f]]\nname = \"b\"",
        """{"f":[{"name":"apple","physical":{"color":"red"},"variety":[{"name":"a"}]},{"name":"b"}]}""")]
    // Whitespace, comments, line endings
    [InlineData("# comment\n\n  a = 1 # trailing\r\n\t[t] # header comment\r\nb = 2\n", """{"a":1,"t":{"b":2}}""")]
    [InlineData("", "{}")]
    public void ParsesValidDocuments(string toml, string expected)
    {
        var actual = CodexToml.Parse(toml);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
    }

    [Fact]
    public void ParsesARealisticCodexConfiguration()
    {
        const string toml = """
            model = "gpt-5.5-codex"
            model_reasoning_effort = "high"
            approval_policy = "on-request"
            sandbox_mode = "workspace-write"
            project_root_markers = [".git", ".hg"]

            [sandbox_workspace_write]
            network_access = true
            writable_roots = ["/tmp/build"]

            [features]
            web_search = true

            [mcp_servers.docs]
            command = "npx"
            args = ["-y", "@example/docs-mcp"]
            env = { "API_HOST" = "https://docs.example.com", TIMEOUT = "30" }
            startup_timeout_sec = 20

            [profiles.fast]
            model = "gpt-5.5-mini"
            model_reasoning_effort = "low"

            [projects."/Users/me/work/repo"]
            trust_level = "trusted"

            [shell_environment_policy]
            inherit = "core"
            exclude = ["AWS_*", "AZURE_*"]
            """;

        var config = CodexToml.Parse(toml);

        Assert.Equal("gpt-5.5-codex", config["model"]?.GetValue<string>());
        Assert.Equal(20, config["mcp_servers"]?["docs"]?["startup_timeout_sec"]?.GetValue<long>());
        Assert.Equal("https://docs.example.com", config["mcp_servers"]?["docs"]?["env"]?["API_HOST"]?.GetValue<string>());
        Assert.Equal("trusted", config["projects"]?["/Users/me/work/repo"]?["trust_level"]?.GetValue<string>());
        Assert.Equal(2, config["shell_environment_policy"]?["exclude"]?.AsArray().Count);
        Assert.Equal("low", config["profiles"]?["fast"]?["model_reasoning_effort"]?.GetValue<string>());
    }

    [Theory]
    // Redefinition rules
    [InlineData("a = 1\na = 2")]
    [InlineData("a.b = 1\na.b = 2")]
    [InlineData("a = 1\na.b = 2")]
    [InlineData("[a]\n[a]")]
    [InlineData("[a]\nb = 1\n[a.b]")]
    [InlineData("a.b = 1\n[a]")]
    [InlineData("[fruit]\napple.color = \"red\"\n[fruit.apple]")]
    [InlineData("[x.y]\n[x]\ny.z = 1")]
    [InlineData("a = { b = 1 }\na.c = 2")]
    [InlineData("a = { b = 1 }\n[a]")]
    [InlineData("a = { b = { c = 1 } }\n[a.b]")]
    [InlineData("a = [1]\n[[a]]")]
    [InlineData("[[a]]\n[a]")]
    [InlineData("[a]\n[[a]]")]
    [InlineData("p = { x = 1, x = 2 }")]
    // Syntax
    [InlineData("a =")]
    [InlineData("a = 1 b = 2")]
    [InlineData("= 1")]
    [InlineData("a.= 1")]
    [InlineData("[a")]
    [InlineData("[[a]")]
    [InlineData("a = [1, 2")]
    [InlineData("a = [1 2]")]
    [InlineData("a = { b = 1")]
    [InlineData("a = \"unterminated")]
    [InlineData("a = \"new\nline\"")]
    [InlineData("a = 'new\nline'")]
    [InlineData("a = \"\"\"never closed")]
    [InlineData("a = \"bad \\q escape\"")]
    [InlineData("a = \"\\uD800\"")]
    [InlineData("a = \"\\U00110000\"")]
    [InlineData("a = \"control \u0001\"")]
    [InlineData("a = 1\rb = 2")]
    [InlineData("a = TRUE")]
    // Numbers
    [InlineData("a = 01")]
    [InlineData("a = 1__0")]
    [InlineData("a = _1")]
    [InlineData("a = 1_")]
    [InlineData("a = 1.")]
    [InlineData("a = .5")]
    [InlineData("a = +0x10")]
    [InlineData("a = 0x")]
    [InlineData("a = 9223372036854775808")]
    [InlineData("a = 0x8000000000000000")]
    [InlineData("a = 1e400")]
    // Values with no JSON form
    [InlineData("a = inf")]
    [InlineData("a = nan")]
    [InlineData("a = 1979-05-27T07:32:00Z")]
    [InlineData("a = 1979-05-27")]
    [InlineData("a = 07:32:00")]
    [InlineData("a = 1979-05-27 07:32:00")]
    public void RejectsInvalidOrUnsupportedDocuments(string toml)
    {
        Assert.Throws<FormatException>(() => CodexToml.Parse(toml));
    }

    [Fact]
    public void BoundsNestingDepth()
    {
        var deep = "a = " + new string('[', 40) + new string(']', 40);

        Assert.Throws<FormatException>(() => CodexToml.Parse(deep));
    }

    [Fact]
    public void ReportsTheLineOfAnError()
    {
        var error = Assert.Throws<FormatException>(() => CodexToml.Parse("a = 1\nb = 2\nc = ?"));

        Assert.StartsWith("TOML line 3:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducesJsonThatRoundTripsThroughSystemTextJson()
    {
        var parsed = CodexToml.Parse("a = 1\nb = [true, \"x\", 1.5]\n[c]\nd = { e = 'f' }");

        using var document = JsonDocument.Parse(parsed.ToJsonString());

        Assert.Equal("f", document.RootElement.GetProperty("c").GetProperty("d").GetProperty("e").GetString());
    }
}
