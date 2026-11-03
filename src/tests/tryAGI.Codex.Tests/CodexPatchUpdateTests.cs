namespace tryAGI.Codex.Tests;

public sealed class CodexPatchUpdateTests
{
    private static CodexPatchHunk Hunk(string body) => Assert.Single(CodexPatch.Parse("*** Begin Patch\n*** Update File: a\n" + body + "\n*** End Patch").Hunks);

    [Theory]
    [InlineData("foo\nbar\nbaz\nqux\n", "@@\n foo\n-bar\n+BAR\n@@\n baz\n-qux\n+QUX", "foo\nBAR\nbaz\nQUX\n")]
    [InlineData("a\nb\nc\nd\ne\nf\n", "@@\n a\n-b\n+B\n@@\n d\n-e\n+E\n@@\n f\n+g\n*** End of File", "a\nB\nc\nd\nE\nf\ng\n")]
    [InlineData("a\nb\n", "@@\n+last", "a\nb\nlast\n")]
    [InlineData("a", "@@\n-a\n+b", "b\n")]
    [InlineData("a\n", "@@\n-a", "")]
    [InlineData("", "@@\n+first", "first\n")]
    [InlineData("same\nsame\n", "@@\n-same\n+last\n*** End of File", "same\nlast\n")]
    public void ReconstructsLegacyUpdates(string original, string patch, string expected) =>
        Assert.Equal(expected, CodexPatch.Update(original, Hunk(patch), cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void ContextAnchorSelectsLaterMethodAndNeverMatchesBeforeIt()
    {
        var hunk = Hunk("@@ method-two\n-old\n+new");
        Assert.Equal("method-one\nold\nmethod-two\nnew\n", CodexPatch.Update("method-one\nold\nmethod-two\nold\n", hunk, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexPatch.Update("old\nmethod-two\n", hunk, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("  old  \n", "old")]
    [InlineData("value — ‘quote’\n", "value - 'quote'")]
    [InlineData("value\u00a0x\n", "value x")]
    public void ReviewedFuzzyMatchesCanLocateSource(string source, string pattern) =>
        Assert.Equal("new\n", CodexPatch.Update(source, Hunk("@@\n-" + pattern + "\n+new"), cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void ExactMatchWinsBeforeEarlierWhitespaceEquivalentMatch()
    {
        Assert.Equal(" old \nnew\n", CodexPatch.Update(" old \nold\n", Hunk("@@\n-old\n+new"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PreserveModeKeepsExactContextTextAndMixedTerminators()
    {
        var source = "before\r\n  context  \nold\rnext\nlast";
        var hunk = Hunk("@@\n context\n-old\n+new\n next");
        Assert.Equal("before\r\n  context  \nnew\r\nnext\nlast\r\n", CodexPatch.Update(source, hunk, CodexPatchUpdateMode.PreserveLineEndings, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\r\nnew\r\n")]
    [InlineData("a\rb\r", "a\rnew\r")]
    [InlineData("a\nb", "a\nnew\n")]
    public void PreserveModeUsesFirstTerminatorForInsertedLines(string source, string expected) =>
        Assert.Equal(expected, CodexPatch.Update(source, Hunk("@@\n a\n-b\n+new"), CodexPatchUpdateMode.PreserveLineEndings, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void ContextIsDistinguishedFromEqualChangedLines()
    {
        Assert.Equal("keep\nchanged\n", CodexPatch.Update("  keep  \r\nkeep\r\n", Hunk("@@\n keep\n-keep\n+changed"), CodexPatchUpdateMode.NormalizeToLf, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("  keep  \r\nchanged\r\n", CodexPatch.Update("  keep  \r\nkeep\r\n", Hunk("@@\n keep\n-keep\n+changed"), CodexPatchUpdateMode.PreserveLineEndings, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TrailingEmptyContextCanMatchFinalNewlineSentinel()
    {
        Assert.Equal("new\n", CodexPatch.Update("old\n", Hunk("@@\n-old\n+new\n "), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("new\r\n", CodexPatch.Update("old\r\n", Hunk("@@\n-old\n+new\n "), CodexPatchUpdateMode.PreserveLineEndings, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LegacyEofLookupRetainsPinnedEarlierRegionBehavior()
    {
        Assert.Equal("a\nX\n", CodexPatch.Update("a\nb\nc\n", Hunk("@@\n-b\n-c\n+X\n@@\n-b\n-c\n+Y\n*** End of File"), CodexPatchUpdateMode.NormalizeToLf, TestContext.Current.CancellationToken));
        // rust-v0.166.0 removed the legacy mode: the end-of-file lookup never starts before the cursor.
        Assert.Throws<CodexPatchException>(() => CodexPatch.Update("a\nb\nc\n", Hunk("@@\n-b\n-c\n+X\n@@\n-b\n-c\n+Y\n*** End of File"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingContextOrLongerPatternFailsWithoutAnyFilesystemEffects()
    {
        Assert.Throws<CodexPatchException>(() => CodexPatch.Update("old\n", Hunk("@@ missing\n-old\n+new"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexPatch.Update("short\n", Hunk("@@\n-short\n-extra\n+new"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<CodexPatchException>(() => CodexPatch.Update(new string('x', CodexPatch.MaximumSourceCharacters + 1), Hunk("@@\n+x"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RepeatedPrefixMatchDoesNotRequireQuadraticSearchAndSupportsCancellation()
    {
        var source = string.Concat(Enumerable.Repeat("a\n", 100_000)) + "end\n";
        var hunk = Hunk("@@\n" + string.Concat(Enumerable.Repeat(" a\n", 50_000)) + "-end\n+new");
        Assert.Equal(string.Concat(Enumerable.Repeat("a\n", 100_000)) + "new\n", CodexPatch.Update(source, hunk, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => CodexPatch.Update(source, hunk, cancellationToken: new CancellationToken(true)));
    }
}
