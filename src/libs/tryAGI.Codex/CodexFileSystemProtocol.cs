using System.Text.Json.Serialization;

namespace tryAGI.Codex;

public sealed record CodexFsPath([property: JsonRequired] string Path);
public sealed record CodexFsReadFile(string DataBase64);
public sealed record CodexFsWriteFile([property: JsonRequired] string Path, [property: JsonRequired] string DataBase64);
public sealed record CodexFsCreateDirectory([property: JsonRequired] string Path, bool? Recursive = null);
public sealed record CodexFsMetadata(bool IsDirectory, bool IsFile, bool IsSymlink, long CreatedAtMs, long ModifiedAtMs);
public sealed record CodexFsDirectoryEntry(string FileName, bool IsDirectory, bool IsFile);
public sealed record CodexFsDirectory(CodexFsDirectoryEntry[] Entries);
public sealed record CodexFsRemove([property: JsonRequired] string Path, bool? Recursive = null, bool? Force = null);
public sealed record CodexFsCopy([property: JsonRequired] string SourcePath,
    [property: JsonRequired] string DestinationPath, bool Recursive = false);
public sealed record CodexFsWatch([property: JsonRequired] string Path, [property: JsonRequired] string WatchId);
public sealed record CodexFsUnwatch([property: JsonRequired] string WatchId);
public sealed record CodexFsChanged(string WatchId, string[] ChangedPaths);
public sealed record CodexFsEmpty;

/// <summary>Trusted host authorization input, never model-selected approval.</summary>
public sealed record CodexFileSystemAccess(string ConnectionId, string Method, string[] ReadPaths, string[] WritePaths);

public sealed class CodexFileSystemException(int code, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Code { get; } = code;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(CodexFsPath))]
[JsonSerializable(typeof(CodexFsReadFile))]
[JsonSerializable(typeof(CodexFsWriteFile))]
[JsonSerializable(typeof(CodexFsCreateDirectory))]
[JsonSerializable(typeof(CodexFsMetadata))]
[JsonSerializable(typeof(CodexFsDirectory))]
[JsonSerializable(typeof(CodexFsRemove))]
[JsonSerializable(typeof(CodexFsCopy))]
[JsonSerializable(typeof(CodexFsWatch))]
[JsonSerializable(typeof(CodexFsUnwatch))]
[JsonSerializable(typeof(CodexFsChanged))]
[JsonSerializable(typeof(CodexFsEmpty))]
public partial class CodexFileSystemJson : JsonSerializerContext;
