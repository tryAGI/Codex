using System.Text.Json;
using tryAGI.Codex;

// Each check exercises a path that breaks under trimming or NativeAOT when metadata is missing.
// Exit code 0 means every check passed in the native binary.
var failures = 0;

void Check(string name, Func<bool> check)
{
    bool passed;
    try
    {
        passed = check();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{name}: {ex.GetType().Name}: {ex.Message}");
        passed = false;
    }

    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    failures += passed ? 0 : 1;
}

Check("embedded protocol baseline", () => CodexProtocolCatalog.Voices.Count > 0);
Check("embedded App Server catalog", () => CodexAppServerProtocol.IsClientRequest("turn/start"));
Check("user input round trip", () =>
{
    using var document = JsonDocument.Parse("""{"type":"image","fileId":"file_1","detail":"high"}""");
    return CodexUserInput.Parse(document.RootElement).ToJson().ToJsonString()
        == """{"type":"image","fileId":"file_1","detail":"high"}""";
});
Check("patch parser", () => CodexPatch.Parse("""
    *** Begin Patch
    *** Add File: a.txt
    +hello
    *** End Patch
    """).Hunks.Count == 1);
Check("source-generated thread JSON", () =>
{
    var info = CodexThreadJson.Default.CodexThreadDocument;
    return info.Properties.Count > 0;
});
Check("TOML configuration", () =>
{
    // Not the system temp folder: on macOS it sits behind a /var symlink, which the reader rejects by design.
    var home = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "codex-aot-" + Guid.NewGuid().ToString("N"))).FullName;
    try
    {
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"probe-model\"\n[features]\nfast = true\n");
        var source = new CodexFileConfigurationSource(new CodexFileConfigurationOptions(home) { IgnoreProjectConfig = true });
        var snapshot = source.LoadAsync(new CodexThreadConfigurationContext("thread/start", null, home),
            new CodexThreadConfigurationSnapshot("account", []), CancellationToken.None).GetAwaiter().GetResult();
        return snapshot.Layers.Any(layer => layer.Config.TryGetProperty("model", out var model) && model.GetString() == "probe-model");
    }
    finally
    {
        Directory.Delete(home, recursive: true);
    }
});

return failures == 0 ? 0 : 1;
