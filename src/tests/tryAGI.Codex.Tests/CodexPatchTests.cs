namespace tryAGI.Codex.Tests;

public sealed class CodexPatchTests
{
    [Fact]
    public void ParsesEveryFileKindAndEnvironmentWithoutExecutingPaths()
    {
        var patch = CodexPatch.Parse("*** Begin Patch\n*** Environment ID: machine-1\n*** Add File: a $(literal).txt\n+Привет\n+\n*** Delete File: b.txt\n*** Update File: c.txt\n*** Move to: nested/d.txt\n@@ method\n old\n-previous\n+next\n*** End of File\n*** End Patch");
        Assert.Equal("machine-1", patch.EnvironmentId);
        Assert.Equal(3, patch.Hunks.Count);
        Assert.Equal("Привет\n\n", patch.Hunks[0].Content);
        Assert.Equal("a $(literal).txt", patch.Hunks[0].Path);
        Assert.Equal(CodexPatchKind.Delete, patch.Hunks[1].Kind);
        Assert.Equal("nested/d.txt", patch.Hunks[2].MoveTo);
        var chunk = Assert.Single(patch.Hunks[2].Chunks);
        Assert.Equal("method", chunk.ChangeContext); Assert.True(chunk.EndOfFile);
        Assert.Equal(["old", "previous"], chunk.OldLines);
        Assert.Equal(["old", "next"], chunk.NewLines);
        Assert.Equal(new CodexPatchContextLine(0, 0), Assert.Single(chunk.ContextLines));
    }

    [Theory]
    [InlineData("<<EOF")]
    [InlineData("<<'EOF'")]
    [InlineData("<<\"EOF\"")]
    public void LiteralHeredocIsParsedAsData(string wrapper) =>
        Assert.Equal("literal\n", Assert.Single(CodexPatch.Parse(wrapper + "\n*** Begin Patch\n*** Add File: a\n+literal\n*** End Patch\nEOF\n").Hunks).Content);

    [Theory]
    [InlineData("bad", null)]
    [InlineData("*** Begin Patch\n*** Add File: a\n+text", null)]
    [InlineData("*** Begin Patch\n*** Update File: a\n*** End Patch", 2)]
    [InlineData("*** Begin Patch\n*** Update File: a\n@@\n*** End Patch", 4)]
    [InlineData("*** Begin Patch\n*** Update File: a\n@@\n@@\n*** End Patch", 4)]
    [InlineData("*** Begin Patch\n*** Add File: a\ntext\n*** End Patch", 3)]
    [InlineData("*** Begin Patch\n*** Delete File: a\n+text\n*** End Patch", 3)]
    [InlineData("*** Begin Patch\n*** Update File: a\n-old\n+new\n*** End of File\n+later\n*** End Patch", 6)]
    public void MalformedInputRetainsDiagnosticLine(string patch, int? line) =>
        Assert.Equal(line, Assert.Throws<CodexPatchException>(() => CodexPatch.Parse(patch)).LineNumber);

    [Fact]
    public void EnvironmentCannotBeEmptyDuplicateOrInsertedAfterFileHeader()
    {
        foreach (var body in new[] { "*** Environment ID: ", "*** Environment ID: a\n*** Environment ID: b", "*** Add File: a\n*** Environment ID: b" })
            Assert.Throws<CodexPatchException>(() => CodexPatch.Parse("*** Begin Patch\n" + body + "\n*** End Patch"));
    }

    [Fact]
    public void ImplicitChunksEmptyContextAndAdjacentFileHeadersMatchGrammar()
    {
        var patch = CodexPatch.Parse("*** Begin Patch\n*** Add File: empty\n*** Update File: a\n\n-old\n+new\n*** Delete File: b\n*** End Patch");
        Assert.Equal("", patch.Hunks[0].Content);
        Assert.Equal(["", "old"], patch.Hunks[1].Chunks[0].OldLines);
        Assert.Empty(CodexPatch.Parse("*** Begin Patch\n*** End Patch").Hunks);
    }

    [Fact]
    public void EveryPossibleChunkBoundaryPreservesUnicodeCrLfAndPatchSemantics()
    {
        const string patch = "*** Begin Patch\r\n*** Add File: a\r\n+Привет 😀\r\n*** Update File: b\r\n@@\r\n-old\r\n+new\r\n*** End Patch";
        var expected = CodexPatch.Parse(patch);
        for (var split = 0; split <= patch.Length; split++)
        {
            var parser = new CodexPatchParser(); parser.Append(patch[..split]); parser.Append(patch[split..]);
            var actual = parser.Finish();
            Assert.Equal(expected.Hunks[0].Content, actual.Hunks[0].Content);
            Assert.Equal(expected.Hunks[1].Chunks[0].NewLines, actual.Hunks[1].Chunks[0].NewLines);
        }
    }

    [Fact]
    public void SnapshotDoesNotMutateAfterAnotherDeltaAndPartialPatchCannotFinish()
    {
        var parser = new CodexPatchParser(); parser.Append("*** Begin Patch\n*** Update File: a\n-old\n");
        var snapshot = parser.Snapshot; parser.Append("+new\n*** End Patch");
        Assert.Empty(snapshot.Hunks[0].Chunks[0].NewLines);
        Assert.Single(parser.Finish().Hunks[0].Chunks[0].NewLines);
        var incomplete = new CodexPatchParser(); incomplete.Append("*** Begin Patch\n");
        Assert.Throws<CodexPatchException>(() => incomplete.Finish());
        Assert.Throws<CodexPatchException>(() => incomplete.Append("*** End Patch"));
    }

    [Fact]
    public void StreamingRejectsTrailingCommandsAndOversizedOrInvalidUnicodeText()
    {
        var parser = new CodexPatchParser(); parser.Append("*** Begin Patch\n*** End Patch\n");
        Assert.Throws<CodexPatchException>(() => parser.Append("do-something\n"));
        Assert.Throws<CodexPatchException>(() => CodexPatch.Parse(new string('x', CodexPatch.MaximumPatchCharacters + 1)));
        Assert.Throws<CodexPatchException>(() => CodexPatch.Parse("*** Begin Patch\n*** Add File: a\n+\ud800\n*** End Patch"));
    }
}
