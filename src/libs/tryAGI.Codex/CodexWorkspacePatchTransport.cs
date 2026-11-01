using System.Text.Json.Serialization;

namespace tryAGI.Codex;

// Host-to-worker protocol, never provider-selected authority or an API endpoint DTO.
public sealed record CodexWorkspacePatchRequest(string Patch, bool DryRun = true,
    CodexWorkspacePatchApproval? Approval = null);
public sealed record CodexWorkspacePatchPrecondition(string Path, string? Sha256, int? Permissions);
public sealed record CodexWorkspacePatchApproval(string Root, string PatchSha256, string PlanSha256,
    IReadOnlyList<CodexWorkspacePatchPrecondition?> Preconditions);
public sealed record CodexWorkspacePatchFileResult(string Path, string Operation, string? BeforeSha256,
    string? AfterSha256, long? BeforeBytes, long? AfterBytes, int? Permissions);
public sealed record CodexWorkspacePatchResult(bool DryRun, CodexWorkspacePatchApproval Approval,
    IReadOnlyList<CodexWorkspacePatchFileResult?> Files, int ProtocolVersion = 1)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodexWorkspaceDiffFile>? DiffFiles { get; init; }
}
public sealed record CodexWorkspaceDiffFile(string Path, string? BeforeText, string? AfterText);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(CodexWorkspacePatchRequest))]
[JsonSerializable(typeof(CodexWorkspacePatchApproval))]
[JsonSerializable(typeof(CodexWorkspacePatchResult))]
public partial class CodexWorkspacePatchJson : JsonSerializerContext;
