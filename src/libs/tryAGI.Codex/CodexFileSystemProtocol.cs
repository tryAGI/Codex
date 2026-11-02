using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Parameters naming one absolute path (<c>fs/readFile</c>, <c>fs/getMetadata</c>, <c>fs/readDirectory</c>).</summary>
/// <param name="Path">Absolute path inside the bound root.</param>
public sealed record CodexFsPath([property: JsonRequired] string Path);

/// <summary>Result of <c>fs/readFile</c>.</summary>
/// <param name="DataBase64">File contents, base64-encoded.</param>
public sealed record CodexFsReadFile(string DataBase64);

/// <summary>Parameters of <c>fs/writeFile</c>.</summary>
/// <param name="Path">Absolute path inside the bound root.</param>
/// <param name="DataBase64">New file contents, base64-encoded.</param>
public sealed record CodexFsWriteFile([property: JsonRequired] string Path, [property: JsonRequired] string DataBase64);

/// <summary>Parameters of <c>fs/createDirectory</c>.</summary>
/// <param name="Path">Absolute path inside the bound root.</param>
/// <param name="Recursive">Create missing parent directories.</param>
public sealed record CodexFsCreateDirectory([property: JsonRequired] string Path, bool? Recursive = null);

/// <summary>Result of <c>fs/getMetadata</c>.</summary>
/// <param name="IsDirectory">The path is a directory.</param>
/// <param name="IsFile">The path is a regular file.</param>
/// <param name="IsSymlink">The path is a symbolic link.</param>
/// <param name="CreatedAtMs">Creation time in Unix milliseconds.</param>
/// <param name="ModifiedAtMs">Last write time in Unix milliseconds.</param>
public sealed record CodexFsMetadata(bool IsDirectory, bool IsFile, bool IsSymlink, long CreatedAtMs, long ModifiedAtMs);

/// <summary>One entry of an <c>fs/readDirectory</c> result.</summary>
/// <param name="FileName">Entry name, without the directory path.</param>
/// <param name="IsDirectory">The entry is a directory.</param>
/// <param name="IsFile">The entry is a regular file.</param>
public sealed record CodexFsDirectoryEntry(string FileName, bool IsDirectory, bool IsFile);

/// <summary>Result of <c>fs/readDirectory</c>.</summary>
/// <param name="Entries">Directory entries.</param>
public sealed record CodexFsDirectory(CodexFsDirectoryEntry[] Entries);

/// <summary>Parameters of <c>fs/remove</c>.</summary>
/// <param name="Path">Absolute path inside the bound root.</param>
/// <param name="Recursive">Remove a non-empty directory and its contents.</param>
/// <param name="Force">Succeed when the path does not exist.</param>
public sealed record CodexFsRemove([property: JsonRequired] string Path, bool? Recursive = null, bool? Force = null);

/// <summary>Parameters of <c>fs/copy</c>.</summary>
/// <param name="SourcePath">Absolute source path inside the bound root.</param>
/// <param name="DestinationPath">Absolute destination path inside the bound root.</param>
/// <param name="Recursive">Copy a directory and its contents.</param>
public sealed record CodexFsCopy([property: JsonRequired] string SourcePath,
    [property: JsonRequired] string DestinationPath, bool Recursive = false);

/// <summary>Parameters of <c>fs/watch</c>.</summary>
/// <param name="Path">Absolute path to watch inside the bound root.</param>
/// <param name="WatchId">Caller-chosen identifier, echoed in change notifications.</param>
public sealed record CodexFsWatch([property: JsonRequired] string Path, [property: JsonRequired] string WatchId);

/// <summary>Parameters of <c>fs/unwatch</c>.</summary>
/// <param name="WatchId">Identifier passed to <c>fs/watch</c>.</param>
public sealed record CodexFsUnwatch([property: JsonRequired] string WatchId);

/// <summary>Payload of the <c>fs/changed</c> notification.</summary>
/// <param name="WatchId">Identifier passed to <c>fs/watch</c>.</param>
/// <param name="ChangedPaths">Absolute paths that changed.</param>
public sealed record CodexFsChanged(string WatchId, string[] ChangedPaths);

/// <summary>Empty result of a method that returns no data.</summary>
public sealed record CodexFsEmpty;

/// <summary>Trusted host authorization input, never model-selected approval.</summary>
/// <param name="ConnectionId">Connection that issued the request.</param>
/// <param name="Method">The <c>fs/*</c> method being authorized.</param>
/// <param name="ReadPaths">Absolute paths the request reads.</param>
/// <param name="WritePaths">Absolute paths the request writes.</param>
public sealed record CodexFileSystemAccess(string ConnectionId, string Method, string[] ReadPaths, string[] WritePaths);

/// <summary>A file system request failed; <see cref="Code"/> is the JSON-RPC error code to return.</summary>
/// <param name="code">JSON-RPC error code.</param>
/// <param name="message">Error message, free of file contents.</param>
/// <param name="inner">Underlying exception, if any.</param>
public sealed class CodexFileSystemException(int code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>JSON-RPC error code.</summary>
    public int Code { get; } = code;
}

/// <summary>Source-generated JSON metadata for the <c>fs/*</c> payloads (camelCase, strict numbers).</summary>
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
