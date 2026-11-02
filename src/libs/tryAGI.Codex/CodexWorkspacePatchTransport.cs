using System.Text.Json.Serialization;

namespace tryAGI.Codex;

// Host-to-worker protocol, never provider-selected authority or an API endpoint DTO.

/// <summary>Asks a workspace host to plan, or to apply a reviewed plan for, one <c>apply_patch</c> text.</summary>
/// <param name="Patch">The patch text in Codex <c>apply_patch</c> grammar.</param>
/// <param name="DryRun">Plan only. Set to <see langword="false"/> together with <paramref name="Approval"/> to apply.</param>
/// <param name="Approval">The approval returned by an earlier dry run; every precondition is rechecked before writing.</param>
public sealed record CodexWorkspacePatchRequest(string Patch, bool DryRun = true,
    CodexWorkspacePatchApproval? Approval = null);

/// <summary>The captured state of one touched path when the plan was made.</summary>
/// <param name="Path">Workspace-relative path.</param>
/// <param name="Sha256">SHA-256 of the original bytes, or <see langword="null"/> when the path was absent.</param>
/// <param name="Permissions">Unix permission bits of the original file, if captured.</param>
public sealed record CodexWorkspacePatchPrecondition(string Path, string? Sha256, int? Permissions);

/// <summary>Binds an approval to one exact patch, plan and set of preconditions.</summary>
/// <param name="Root">Absolute workspace root the plan was made against.</param>
/// <param name="PatchSha256">SHA-256 of the patch text.</param>
/// <param name="PlanSha256">SHA-256 of the planned changes.</param>
/// <param name="Preconditions">Captured state of every touched path.</param>
public sealed record CodexWorkspacePatchApproval(string Root, string PatchSha256, string PlanSha256,
    IReadOnlyList<CodexWorkspacePatchPrecondition?> Preconditions);

/// <summary>The planned or applied change to one file.</summary>
/// <param name="Path">Workspace-relative path.</param>
/// <param name="Operation"><c>add</c>, <c>update</c>, <c>delete</c> or <c>move</c>.</param>
/// <param name="BeforeSha256">SHA-256 before the change, or <see langword="null"/> when absent.</param>
/// <param name="AfterSha256">SHA-256 after the change, or <see langword="null"/> when deleted.</param>
/// <param name="BeforeBytes">Size before the change.</param>
/// <param name="AfterBytes">Size after the change.</param>
/// <param name="Permissions">Unix permission bits of the result.</param>
public sealed record CodexWorkspacePatchFileResult(string Path, string Operation, string? BeforeSha256,
    string? AfterSha256, long? BeforeBytes, long? AfterBytes, int? Permissions);

/// <summary>The host's answer to a <see cref="CodexWorkspacePatchRequest"/>.</summary>
/// <param name="DryRun">Nothing was written.</param>
/// <param name="Approval">The approval to send back to apply exactly this plan.</param>
/// <param name="Files">Per-file changes.</param>
/// <param name="ProtocolVersion">Transport version.</param>
public sealed record CodexWorkspacePatchResult(bool DryRun, CodexWorkspacePatchApproval Approval,
    IReadOnlyList<CodexWorkspacePatchFileResult?> Files, int ProtocolVersion = 1)
{
    /// <summary>Before/after text of changed text files, for review UIs. Omitted when not requested.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodexWorkspaceDiffFile>? DiffFiles { get; init; }
}

/// <summary>Before/after text of one changed file.</summary>
/// <param name="Path">Workspace-relative path.</param>
/// <param name="BeforeText">Text before the change, or <see langword="null"/> when absent.</param>
/// <param name="AfterText">Text after the change, or <see langword="null"/> when deleted.</param>
public sealed record CodexWorkspaceDiffFile(string Path, string? BeforeText, string? AfterText);

/// <summary>Source-generated JSON metadata for the workspace patch transport (camelCase, strict numbers).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(CodexWorkspacePatchRequest))]
[JsonSerializable(typeof(CodexWorkspacePatchApproval))]
[JsonSerializable(typeof(CodexWorkspacePatchResult))]
public partial class CodexWorkspacePatchJson : JsonSerializerContext;
