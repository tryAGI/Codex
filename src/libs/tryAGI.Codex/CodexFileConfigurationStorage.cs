using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Concrete Unix user config storage using the existing file-layer loader and source-preserving TOML editor.
/// All cooperating writers must use its persistent file lease. The host owns the private CODEX_HOME directory,
/// path ancestry, account/policy sources and commit-time policy lease. Uncoordinated external editors are detected
/// by preimage checks where possible, not claimed to participate in the CAS. No arbitrary RPC file paths are opened.</summary>
public sealed partial class CodexFileConfigurationStorage : ICodexConfigurationWriteStorage
{
    private const int MaximumBytes = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode NonOwner = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly CodexFileConfigurationSource source;
    private readonly string home;
    private readonly string file;
    private readonly string lockPath;
    private readonly Func<CancellationToken, Task<CodexThreadConfigurationSnapshot>> account;
    private readonly Func<CodexThreadConfigurationLayer, CodexConfigurationRpcLayerMetadata> describeLayer;
    private readonly Func<CodexConfigurationWriteSnapshot, CodexResolvedConfiguration, CancellationToken, ValueTask<IAsyncDisposable?>> authorize;
    private readonly Func<CodexThreadConfigurationSnapshot, CancellationToken, Task>? reload;
    private readonly IReadOnlySet<string> writableFeatures;

    private sealed record UserFile(string Version, string Text, bool Bom, JsonObject Raw);
    private sealed record Capture(UserFile User, CodexConfigurationWriteSnapshot View);

    /// <summary>Creates a user config storage adapter. Non-user layer descriptors must come from the actual host
    /// sources, not request parameters. The commit policy callback validates the complete effective candidate and holds
    /// its policy generation until disposal (null denies). Neither callback may re-enter this file lease.
    /// The optional reload consumer is called after durable persistence with the freshly loaded stack; it may update
    /// runtime settings only, not session-static model defaults or already admitted work.</summary>
    public CodexFileConfigurationStorage(CodexFileConfigurationOptions options,
        Func<CancellationToken, Task<CodexThreadConfigurationSnapshot>> account,
        Func<CodexThreadConfigurationLayer, CodexConfigurationRpcLayerMetadata> describeLayer,
        IReadOnlySet<string> writableFeatures,
        Func<CodexConfigurationWriteSnapshot, CodexResolvedConfiguration, CancellationToken, ValueTask<IAsyncDisposable?>> authorize,
        Func<CodexThreadConfigurationSnapshot, CancellationToken, Task>? reload = null)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The durable TOML adapter requires macOS or Linux.");
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(describeLayer);
        ArgumentNullException.ThrowIfNull(writableFeatures);
        ArgumentNullException.ThrowIfNull(authorize);
        if (!Path.IsPathFullyQualified(options.CodexHome)) throw new ArgumentException("A private absolute CODEX_HOME is required.");
        home = Path.GetFullPath(options.CodexHome);
        file = Path.Combine(home, "config.toml");
        lockPath = Path.Combine(home, ".config.toml.native.lock");
        source = new(options with
        {
            CodexHome = home, IgnoreProjectConfig = true,
            CloudConfigFiles = options.CloudConfigFiles.ToArray(), RequirementsFiles = options.RequirementsFiles.ToArray()
        });
        this.account = account;
        this.describeLayer = describeLayer;
        this.writableFeatures = writableFeatures.ToFrozenSet(StringComparer.Ordinal);
        this.authorize = authorize;
        this.reload = reload;
        EnsureHome();
    }

    /// <inheritdoc />
    public async Task<CodexConfigurationWriteSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return (await CaptureAsync(cancellationToken).ConfigureAwait(false)).View;
    }

    /// <summary>Reads the same thread-agnostic source and exact user-file version for config/read.
    /// Project-scoped reads require a separate authenticated cwd-aware reader.</summary>
    public async Task<CodexConfigurationRpcSnapshot> ReadConfigurationAsync(string? cwd, CancellationToken cancellationToken)
    {
        if (cwd is not null) throw new CodexThreadRpcException(-32602, "Project-scoped configuration reads require a registered host reader.");
        return (await ReadAsync(cancellationToken).ConfigureAwait(false)).Configuration;
    }

    /// <inheritdoc />
    public async Task<string?> TryCommitAsync(CodexConfigurationWriteCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.FilePath != file) throw Reject(CodexConfigurationWriteFailure.ConfigLayerReadonly);
        if (candidate.ReloadUserConfig && reload is null) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        if (candidate.UserConfig.ValueKind != JsonValueKind.Object || Utf8.GetByteCount(candidate.UserConfig.GetRawText()) > MaximumBytes)
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);

        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var capture = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = capture.View.Configuration.Snapshot;
        if (snapshot.Revision != candidate.ExpectedStackRevision || capture.User.Version != candidate.ExpectedUserVersion) return null;
        var layers = snapshot.Layers.ToArray();
        var index = Array.FindIndex(layers, layer => layer.Kind == CodexConfigurationLayerKind.User && layer.Name == file);
        if (index < 0) throw Reject(CodexConfigurationWriteFailure.UserLayerNotFound);
        var prior = CodexConfigurationResolver.Resolve(snapshot);
        CodexConfigurationWriteRpc.ValidateCandidate(layers[index].Config, candidate.UserConfig, prior.Config,
            snapshot.Requirements, writableFeatures);

        // Preserve discovery-only keys removed by the loader, as well as all unrelated TOML text.
        var raw = (JsonObject)capture.User.Raw.DeepClone();
        foreach (var property in layers[index].Config.EnumerateObject()) raw.Remove(property.Name);
        foreach (var property in candidate.UserConfig.EnumerateObject())
            raw[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        var rewritten = CodexTomlEdit.Rewrite(capture.User.Text, raw);
        byte[] bytes;
        try { bytes = Utf8.GetBytes((capture.User.Bom ? "\uFEFF" : "") + rewritten); }
        catch (EncoderFallbackException) { throw Reject(CodexConfigurationWriteFailure.ConfigValidationError); }
        if (bytes.Length > MaximumBytes) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        layers[index] = layers[index] with { Config = candidate.UserConfig.Clone() };
        var resolved = CodexConfigurationResolver.Resolve(snapshot with { Layers = layers });
        await using var policy = await authorize(capture.View, resolved, cancellationToken).ConfigureAwait(false);
        if (policy is null) throw Reject(CodexConfigurationWriteFailure.ConfigRequirementReadonly);

        // The captured policy and all config layers must still match immediately before commit.
        var latest = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (latest.View.Configuration.Snapshot.Revision != snapshot.Revision || latest.User.Version != capture.User.Version)
            return null;
        var version = Version(bytes);
        if (version != capture.User.Version)
            await WriteAsync(bytes, capture.User.Version, cancellationToken).ConfigureAwait(false);

        if (candidate.ReloadUserConfig)
        {
            // A failed reload after a durable file commit is uncertain to the caller, not
            // an instruction to replay the write. Host must reconcile via a fresh read.
            var committed = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (committed.User.Version != version) throw new IOException("Configuration reload generation changed.");
            await reload!(committed.View.Configuration.Snapshot, cancellationToken).ConfigureAwait(false);
        }
        return version;
    }

    private async Task<Capture> CaptureAsync(CancellationToken ct)
    {
        var before = await ReadUserAsync(ct).ConfigureAwait(false);
        var host = await account(ct).ConfigureAwait(false);
        var snapshot = await source.LoadAsync(new("config/read", null, home), host, ct).ConfigureAwait(false);
        var after = await ReadUserAsync(ct).ConfigureAwait(false);
        if (before.Version != after.Version) throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);
        var layers = snapshot.Layers.ToList();
        var expected = (JsonObject)before.Raw.DeepClone();
        CodexFileConfigurationSource.RemoveDiscoveryKeys(expected);
        var matches = layers.Where(layer => layer.Kind == CodexConfigurationLayerKind.User && layer.Name == file).ToArray();
        if (matches.Length > 1 || (before.Version == "missing" ? matches.Length != 0 :
            matches.Length != 1 || !JsonNode.DeepEquals(expected, JsonNode.Parse(matches[0].Config.GetRawText()))))
            throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);

        if (matches.Length == 0)
        {
            var insert = layers.FindIndex(layer => layer.Kind >= CodexConfigurationLayerKind.User);
            layers.Insert(insert < 0 ? layers.Count : insert, new(CodexConfigurationLayerKind.User, file, Element(expected)));
        }
        var metadata = new List<CodexConfigurationRpcLayerMetadata>(layers.Count);
        foreach (var layer in layers)
        {
            var item = layer.Kind == CodexConfigurationLayerKind.User && layer.Name == file
                ? new CodexConfigurationRpcLayerMetadata(layer.Kind, file, before.Version,
                    Element(new JsonObject { ["type"] = "user", ["file"] = file, ["profile"] = null }))
                : describeLayer(layer);
            CodexConfigurationReadRpc.ValidateSource(item);
            if (item.Kind != layer.Kind || item.LayerName != layer.Name || string.IsNullOrWhiteSpace(item.Version))
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            metadata.Add(item);
        }
        // Include the loader's full source revision and the exact writable file preimage.
        var view = new CodexConfigurationRpcSnapshot(snapshot with
        {
            Revision = Version(Utf8.GetBytes(snapshot.Revision + ":" + before.Version + ":" +
                (snapshot.Requirements?.GetRawText() ?? "null") + ":" +
                string.Join("\n", metadata.Select(item => item.Version.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + item.Version)))),
            Layers = Array.AsReadOnly(layers.ToArray())
        }, metadata.AsReadOnly());
        return new(before, new(view, file, file, writableFeatures, reload is not null));
    }

    private async Task<UserFile> ReadUserAsync(CancellationToken ct)
    {
        EnsureHome();
        CheckFile(file);
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
                throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);
            var text = Utf8.GetString(bytes);
            var bom = text.StartsWith('\uFEFF');
            if (bom) text = text[1..];
            return new(Version(bytes), text, bom, CodexToml.Parse(text));
        }
        catch (FileNotFoundException) { return new("missing", "", false, new JsonObject()); }
        catch (Exception error) when (error is DecoderFallbackException or FormatException)
        { throw Reject(CodexConfigurationWriteFailure.ConfigValidationError); }
    }

    private async Task WriteAsync(byte[] bytes, string expectedVersion, CancellationToken ct)
    {
        var temp = Path.Combine(home, ".config.toml." + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemp = false;
        try
        {
            var options = new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            await using (var stream = new FileStream(temp, options))
            {
                ownsTemp = true;
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if ((await ReadUserAsync(ct).ConfigureAwait(false)).Version != expectedVersion)
                throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, file, overwrite: true);
            ownsTemp = false;
            SyncDirectory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("User configuration commit outcome is unconfirmed."); }
        finally
        {
            if (ownsTemp)
            {
                try { File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            EnsureHome();
            CheckFile(lockPath);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
                return new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                    throw new IOException("User configuration lease is busy.");
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
    }

    private void EnsureHome()
    {
        CodexFileConfigurationSource.RejectLinks(home);
        var attributes = File.GetAttributes(home);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0 ||
            OperatingSystem.IsWindows() || (File.GetUnixFileMode(home) & NonOwner) != 0)
            throw Reject(CodexConfigurationWriteFailure.ConfigLayerReadonly);
    }

    private static void CheckFile(string path)
    {
        CodexFileConfigurationSource.RejectLinks(path);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw Reject(CodexConfigurationWriteFailure.ConfigLayerReadonly);
        }
        catch (FileNotFoundException) { }
    }

    private void SyncDirectory()
    {
        EnsureHome();
        var descriptor = OpenDirectory(home, 0);
        if (descriptor < 0) throw new IOException("Configuration directory could not be synchronized.");
        try
        {
            while (Sync(descriptor) != 0)
                if (Marshal.GetLastPInvokeError() != 4) throw new IOException("Configuration directory could not be synchronized.");
        }
        finally { _ = Close(descriptor); }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Sync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    private static string Version(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonElement Element(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexConfigurationWriteException Reject(CodexConfigurationWriteFailure failure) => new(failure);
}
