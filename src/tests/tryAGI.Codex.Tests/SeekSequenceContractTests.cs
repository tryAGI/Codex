namespace tryAGI.Codex.Tests;

/// <summary>
/// Replays the cases of upstream <c>codex-rs/apply-patch/src/seek_sequence.rs</c> (reviewed at
/// <c>rust-v0.166.0</c>) against <see cref="CodexPatch"/>'s line matcher, plus the end-of-file and
/// Unicode rules its implementation states but its own tests do not pin.
/// </summary>
public sealed class SeekSequenceContractTests
{
    public static TheoryData<string[], string[], int, bool, int?> Cases => new()
    {
        // Upstream unit tests.
        { ["foo", "bar", "baz"], ["bar", "baz"], 0, false, 1 },
        { ["foo   ", "bar\t\t"], ["foo", "bar"], 0, false, 0 },
        { ["    foo   ", "   bar\t"], ["foo", "bar"], 0, false, 0 },
        { ["just one line"], ["too", "many", "lines"], 0, false, null },
        // An empty pattern matches at the cursor.
        { ["a", "b"], [], 1, false, 1 },
        // Matching never starts before the cursor.
        { ["x", "y", "x"], ["x"], 1, false, 2 },
        // End of file: the pattern is matched at the end, not at an earlier equal region.
        { ["end", "middle", "end"], ["end"], 0, true, 2 },
        // End of file never moves the search before the cursor (rust-v0.166.0 removed the legacy exception).
        { ["a", "b", "c"], ["b", "c"], 2, true, null },
        // End of file does not fall back to an earlier match.
        { ["end", "tail"], ["end"], 0, true, null },
        // A stricter pass wins over an earlier, more lenient match.
        { [" old ", "old"], ["old"], 0, false, 1 },
        // The final pass maps typographic punctuation and odd spaces to ASCII.
        { ["value — ‘quote’ “hi” x"], ["value - 'quote' \"hi\" x"], 0, false, 0 },
        { ["a　b"], ["a b"], 0, false, 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheUpstreamSequenceRules(string[] lines, string[] pattern, int start, bool eof, int? expected) =>
        Assert.Equal(expected, CodexPatch.Seek(lines, pattern, start, eof, CodexPatchUpdateMode.PreserveLineEndings, TestContext.Current.CancellationToken));
}
