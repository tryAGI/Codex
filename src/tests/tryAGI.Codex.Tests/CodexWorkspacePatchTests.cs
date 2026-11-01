using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexWorkspacePatchTests
{
    private static string Patch(string body) => "*** Begin Patch\n" + body + "\n*** End Patch";
    private static CodexWorkspaceFile File(string path, string content, int? mode = null) => new(path, Encoding.UTF8.GetBytes(content), mode);
    private static string Text(CodexWorkspaceChange change) => Encoding.UTF8.GetString((change.AfterContents ?? throw new InvalidDataException("Required data was null.")).Span);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void SequentialUpdateMoveAndFurtherUpdateUseVirtualContentsAndAllOriginalHashes()
    {
        var a = Encoding.UTF8.GetBytes("old\n");
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n-old\n+middle\n*** Update File: a\n*** Move to: b\n-middle\n+moved\n*** Update File: b\n-moved\n+final"), [new("a", a, 0x1ed), new("b", null)], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, plan.Changes.Count);
        var removed = Assert.Single(plan.Changes, c => c.Path == "a");
        var created = Assert.Single(plan.Changes, c => c.Path == "b");
        Assert.Equal("delete", removed.Operation); Assert.Null(removed.AfterContents);
        Assert.Equal("create", created.Operation); Assert.Equal("final\n", Text(created)); Assert.Equal(0x1ed, created.Permissions);
        Assert.Equal(Hash(a), plan.Preconditions["a"]); Assert.Null(plan.Preconditions["b"]);
    }

    [Fact]
    public void AddThenDeleteNetNoopStillRequiresAbsentPathAtDispatch()
    {
        var plan = CodexWorkspacePatch.Plan(Patch("*** Add File: a\n+new\n*** Delete File: a"), [new("a", null)], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(plan.Changes); Assert.Single(plan.Preconditions); Assert.Null(plan.Preconditions["a"]);
    }

    [Fact]
    public void EmptyFileCreationIsDistinctFromDeletionAndAnAbsentOriginal()
    {
        var created = Assert.Single(CodexWorkspacePatch.Plan(Patch("*** Add File: a"), [new("a", null)], cancellationToken: TestContext.Current.CancellationToken).Changes);
        Assert.True(created.AfterContents.HasValue); Assert.Empty(created.AfterContents.Value.ToArray());
        Assert.Equal("create", created.Operation);
        var deleted = Assert.Single(CodexWorkspacePatch.Plan(Patch("*** Delete File: a"), [new("a", ReadOnlyMemory<byte>.Empty)], cancellationToken: TestContext.Current.CancellationToken).Changes);
        Assert.False(deleted.AfterContents.HasValue); Assert.NotNull(deleted.BeforeSha256);
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Delete File: a"), [new("a", null)], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MoveOverExistingDestinationRequiresBothOriginalHashesAndPreservesDestinationMode()
    {
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n*** Move to: b\n-old\n+new"), [File("a", "old\n", 0x1ed), File("b", "other\n", 0x180)], cancellationToken: TestContext.Current.CancellationToken);
        var target = Assert.Single(plan.Changes, c => c.Path == "b");
        Assert.Equal("update", target.Operation); Assert.Equal(0x180, target.Permissions); Assert.Equal("new\n", Text(target));
        Assert.All(plan.Preconditions.Values, hash => Assert.NotNull(hash));
    }

    [Fact]
    public void MoveChainDoesNotLoseOriginalOrIntermediatePreconditions()
    {
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n*** Move to: b\n-x\n+y\n*** Update File: b\n*** Move to: c\n-y\n+z"), [File("a", "x\n", 0x1ed), new("b", null), new("c", null)], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, plan.Preconditions.Count); Assert.Equal(2, plan.Changes.Count);
        Assert.Equal("z\n", Text(Assert.Single(plan.Changes, c => c.Path == "c")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void UpdatePreservesBomAndLineEnding(string ending)
    {
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("a" + ending + "old" + ending + "z" + ending)).ToArray();
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n a\n-old\n+новое 😀\n z"), [new("a", bytes, 0x1a4)], cancellationToken: TestContext.Current.CancellationToken);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("a" + ending + "новое 😀" + ending + "z" + ending)), (change.AfterContents ?? throw new InvalidDataException("Required data was null.")).ToArray());
        Assert.Equal(Hash(bytes), change.BeforeSha256);
    }

    [Fact]
    public void MixedLineEndingsRemainDistinctOutsideUpdatedRegion()
    {
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n-old\n+new"), [File("a", "first\r\nold\nlast\r")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("first\r\nnew\r\nlast\r", Text(Assert.Single(plan.Changes)));
    }

    [Fact]
    public void CapturedBytesAndReturnedBytesCannotModifyPlan()
    {
        var bytes = Encoding.UTF8.GetBytes("old\n");
        var plan = CodexWorkspacePatch.Plan(Patch("*** Update File: a\n-old\n+new"), [new("a", bytes)], cancellationToken: TestContext.Current.CancellationToken);
        var hash = Hash(bytes); bytes[0] = (byte)'X';
        var change = Assert.Single(plan.Changes); var exposed = (change.AfterContents ?? throw new InvalidDataException("Required data was null."));
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(exposed, out var array); (array.Array ?? throw new InvalidDataException("Required data was null."))[0] = (byte)'X';
        Assert.Equal(hash, change.BeforeSha256); Assert.Equal("new\n", Text(change));
    }

    [Theory]
    [InlineData("../a")]
    [InlineData("a/../b")]
    [InlineData("/a")]
    [InlineData("C:/a")]
    [InlineData("a\\b")]
    [InlineData("./a")]
    [InlineData("a//b")]
    public void TraversalAndAmbiguousPathsNeverProducePlan(string path) => Assert.Throws<CodexPatchException>(() =>
        CodexWorkspacePatch.Plan(Patch("*** Add File: " + path + "\n+x"), [new(path, null)], cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void MissingDestinationCaptureAndUnexpectedCaptureAreRejected()
    {
        var patch = Patch("*** Update File: a\n*** Move to: b\n-old\n+new");
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(patch, [File("a", "old\n")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(patch, [File("a", "old\n"), new("other", null)], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(patch, [File("a", "old\n"), new("b", null), new("extra", null)], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CaseInsensitiveSourceDestinationAliasCannotRemoveFile()
    {
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Update File: a\n*** Move to: A\n-old\n+new"), [File("a", "old\n")], new() { CaseSensitivePaths = false }, TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Add File: a\n+x\n*** Add File: b\n+y"), [File("a", "x"), File("A", "y")], new() { CaseSensitivePaths = false }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LateFailedHunkDoesNotMutateOriginalSnapshots()
    {
        var bytes = Encoding.UTF8.GetBytes("old\n");
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Update File: a\n-old\n+new\n*** Update File: b\n-missing\n+oops"), [new("a", bytes), File("b", "other\n")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("old\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BinaryDeletionIsAllowedButBinaryUpdateFailsBeforeEffects()
    {
        var invalid = new byte[] { 0xff, 0xfe, 0xfd };
        var deleted = CodexWorkspacePatch.Plan(Patch("*** Delete File: a"), [new("a", invalid)], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("delete", Assert.Single(deleted.Changes).Operation);
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Update File: a\n-x\n+y"), [new("a", invalid)], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EnvironmentRequiresExplicitHostBinding()
    {
        var patch = Patch("*** Environment ID: bound\n*** Add File: a\n+x");
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(patch, [new("a", null)], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(CodexWorkspacePatch.Plan(patch, [new("a", null)], new() { EnvironmentId = "bound" }, TestContext.Current.CancellationToken).Changes);
    }

    [Fact]
    public void BudgetAndCancellationRejectBeforeReturningPlan()
    {
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Add File: a\n+large"), [new("a", null)], new() { MaximumTotalBytes = 2 }, TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexWorkspacePatch.Plan(Patch("*** Delete File: a"), [File("a", "large")], new() { MaximumTotalBytes = 2 }, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CodexWorkspacePatch.Plan(Patch("*** Add File: a\n+x"), [new("a", null)], cancellationToken: cancelled.Token));
    }
}
