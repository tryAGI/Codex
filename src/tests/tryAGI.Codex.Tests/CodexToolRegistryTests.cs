using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexToolRegistryTests
{
    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return doc.RootElement.Clone();
    }

    private static CodexRegisteredTool Tool(string? ns, string name, CodexToolExposure exposure,
        string kind = "function", string? searchText = null) =>
        new(new CodexToolIdentity(ns, name),
            Json($$"""{"type":"{{kind}}","name":"{{name}}","description":"example","parameters":{"type":"object"} }"""),
            exposure, SearchText: searchText);

    [Fact]
    public void DefaultNamespacesCollideButDistinctNamespacesAndCaseRemainSeparate()
    {
        Assert.Throws<ArgumentException>(() => new CodexToolRegistry([
            Tool(null, "read", CodexToolExposure.Direct),
            Tool("functions", "read", CodexToolExposure.Deferred)]));
        Assert.Throws<ArgumentException>(() => new CodexToolRegistry([
            Tool("", "read", CodexToolExposure.Direct),
            Tool(null, "read", CodexToolExposure.Hidden)]));
        var registry = new CodexToolRegistry([
            Tool(null, "read", CodexToolExposure.Direct),
            Tool("files", "read", CodexToolExposure.Direct),
            Tool("files", "Read", CodexToolExposure.Direct)]);
        Assert.Equal(3, CodexToolCalls.Advertised(registry.CreateModelTools(), true).Count);
    }

    [Fact]
    public void SearchCoalescesNamespacesWithoutAdmittingDeferredTools()
    {
        var first = Tool("files", "read", CodexToolExposure.Deferred, searchText: "read file");
        var second = Tool("files", "write", CodexToolExposure.DeferredModelOnly, searchText: "write file");
        var registry = new CodexToolRegistry([
            Tool(null, "clock", CodexToolExposure.Direct), first, second,
            Tool("files", "secret", CodexToolExposure.Hidden, searchText: "file"),
            Tool("files", "nested", CodexToolExposure.CodeModeOnly, searchText: "file")]);
        var result = Assert.Single(registry.SearchModelTools("file"));
        Assert.Equal("namespace", result.GetProperty("type").GetString());
        Assert.Equal("files", result.GetProperty("name").GetString());
        var children = result.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, child => Assert.True(child.GetProperty("defer_loading").GetBoolean()));
        Assert.Single(registry.CreateModelTools());
        Assert.Single(registry.Search("file", codeMode: true));
        Assert.Contains(registry.CodeModeTools(), x => x.Identity.Name == "nested");
        Assert.DoesNotContain(registry.CodeModeTools(), x => x.Identity == second.Identity);

        var call = Json("""{"type":"function_call","call_id":"1","name":"read","namespace":"files","arguments":"{}"}""");
        Assert.Throws<CodexProtocolException>(() => CodexToolCalls.Validate(call,
            CodexToolCalls.Advertised(registry.CreateModelTools(), true), true));
        var admitted = CodexToolCalls.Advertised(registry.CreateModelTools([first.Identity]), true);
        Assert.Equal("read", CodexToolCalls.Validate(call, admitted, true).Name);
        Assert.Throws<ArgumentException>(() => registry.CreateModelTools([new("files", "secret")]));
    }

    [Fact]
    public void SearchDefaultsAndLimitsAreDeterministic()
    {
        var registry = new CodexToolRegistry(Enumerable.Range(0, 12)
            .Select(i => Tool(null, $"tool{i:D2}", CodexToolExposure.Deferred, searchText: "document")));
        Assert.Equal(8, registry.Search("document").Count);
        Assert.Equal(new[] { "tool00", "tool01" }, registry.Search("document", 2).Select(x => x.Identity.Name));
        Assert.Empty(registry.Search("nonexistent"));
        Assert.Throws<ArgumentException>(() => registry.Search(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Search("document", 0));
        var ns = Assert.Single(registry.SearchModelTools("document", 1));
        Assert.Equal("functions", ns.GetProperty("name").GetString());
    }

    [Fact]
    public void CustomAndFunctionDefinitionsRetainKindsAndSnapshotInput()
    {
        CodexToolRegistry registry;
        using (var doc = JsonDocument.Parse("""{"type":"custom","name":"patch","format":{"type":"text"},"future":1}"""))
            registry = new([new(new(null, "patch"), doc.RootElement, CodexToolExposure.DirectModelOnly)]);
        var spec = Assert.Single(registry.CreateModelTools());
        Assert.Equal(1, spec.GetProperty("future").GetInt32());
        Assert.Equal(CodexToolCallKind.Custom, CodexToolCalls.Advertised([spec], true)[("functions", "patch")]);
        Assert.Empty(registry.CodeModeTools());
        Assert.Throws<ArgumentException>(() => new CodexToolRegistry([
            new(new(null, "other"), spec, CodexToolExposure.Direct)]));
    }
}
