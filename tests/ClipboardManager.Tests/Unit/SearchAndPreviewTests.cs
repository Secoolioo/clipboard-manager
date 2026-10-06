using ClipboardManager.Core.History;
using ClipboardManager.Core.Search;
using ClipboardManager.Core.Text;

namespace ClipboardManager.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class SearchAndPreviewTests
{
    private static HistoryEntry Entry(long id, string text, long? pinned = null) =>
        new(id, text, text.Length, TextMetrics.CountLines(text), id, id, pinned);

    [Fact]
    public void Empty_query_returns_everything_unchanged()
    {
        var snapshot = new HistorySnapshot([Entry(1, "a", 1)], [Entry(2, "b")]);
        Assert.Same(snapshot, HistorySearch.Filter(snapshot, "   "));
    }

    [Fact]
    public void All_terms_must_match_case_insensitively_in_any_order()
    {
        var snapshot = new HistorySnapshot([], [Entry(1, "docker compose up -d"), Entry(2, "docker ps"), Entry(3, "Compose file")]);

        var result = HistorySearch.Filter(snapshot, "UP docker");

        Assert.Equal([1L], result.History.Select(e => e.Id));
    }

    [Fact]
    public void Search_handles_umlauts_and_emoji()
    {
        var snapshot = new HistorySnapshot([], [Entry(1, "Grüße aus München 👋"), Entry(2, "hello")]);

        Assert.Single(HistorySearch.Filter(snapshot, "MÜNCHEN").History);
        Assert.Single(HistorySearch.Filter(snapshot, "👋").History);
    }

    [Fact]
    public void Pinned_and_history_matches_stay_in_their_groups()
    {
        var snapshot = new HistorySnapshot([Entry(1, "ssh admin@203.0.113.10", 5)], [Entry(2, "ssh-keygen -t ed25519")]);

        var result = HistorySearch.Filter(snapshot, "ssh");

        Assert.Single(result.Pinned);
        Assert.Single(result.History);
    }

    [Fact]
    public void Five_thousand_typical_entries_search_fast()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => Entry(i, $"entry {i} git commit -m 'fix {i}' https://example.com/{i}")).ToList();
        var snapshot = new HistorySnapshot([], entries);
        HistorySearch.Filter(snapshot, "warm up");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            HistorySearch.Filter(snapshot, "example 4999");
        }

        // Generous bound so CI noise does not flake; typical is far below 1 ms per query.
        Assert.True(watch.Elapsed.TotalMilliseconds / 20 < 25, $"search took {watch.Elapsed.TotalMilliseconds / 20:F2} ms");
    }

    [Theory]
    [InlineData("docker compose up -d", "docker compose up -d")]
    [InlineData("  padded  ", "··padded··")]
    [InlineData("cmd\n", "cmd ⏎")]
    [InlineData("\n\n  first line\nsecond", "first line")]
    [InlineData("tabs\t\tand   spaces", "tabs and spaces")]
    [InlineData("   ", "···")]
    public void Row_preview_makes_whitespace_visible(string text, string expected)
    {
        Assert.Equal(expected, PreviewText.ForRow(text));
    }

    [Fact]
    public void Row_preview_truncates_long_text()
    {
        var preview = PreviewText.ForRow(new string('a', 500), maxLength: 20);
        Assert.Equal(20, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Snippet_starts_near_a_late_match()
    {
        var text = new string('x', 200) + " needle here";
        var snippet = PreviewText.Snippet(text, text.IndexOf("needle", StringComparison.Ordinal));

        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains("needle", snippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", ContentKind.Url)]
    [InlineData("mailto:someone@example.com", ContentKind.Url)]
    [InlineData("C:\\Users\\Public\\file.txt", ContentKind.Path)]
    [InlineData("\\\\server\\share\\dir", ContentKind.Path)]
    [InlineData("~/projects/app", ContentKind.Path)]
    [InlineData("and/or", ContentKind.Text)]
    [InlineData("1/2", ContentKind.Text)]
    [InlineData("see https://example.com", ContentKind.Text)]
    [InlineData("https://example.com\nsecond line", ContentKind.Text)]
    [InlineData("javascript:alert(1)", ContentKind.Text)]
    public void Classification_is_strict(string text, ContentKind expected)
    {
        Assert.Equal(expected, ContentClassifier.Classify(text));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("one\n", 1)]
    [InlineData("one\r\ntwo", 2)]
    [InlineData("a\rb\nc", 3)]
    public void Line_count(string text, int expected) => Assert.Equal(expected, TextMetrics.CountLines(text));
}
