namespace QuotaBar.Tests;

[Collection("data")]
public class CostScanTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

    static string Line(string id, string model, DateTimeOffset at, long input = 0, long output = 0, long read = 0, long w5 = 0, long w1h = 0, string speed = "standard") =>
        $$$$"""{"type":"assistant","requestId":"req_{{{{id}}}}","timestamp":"{{{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}}}","message":{"id":"msg_{{{{id}}}}","model":"{{{{model}}}}","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":{{{{input}}}},"output_tokens":{{{{output}}}},"cache_read_input_tokens":{{{{read}}}},"cache_creation_input_tokens":{{{{w5 + w1h}}}},"cache_creation":{"ephemeral_5m_input_tokens":{{{{w5}}}},"ephemeral_1h_input_tokens":{{{{w1h}}}}},"speed":"{{{{speed}}}}"}}}""" + "\n";

    static string Root(TempData d)
    {
        var root = Path.Combine(d.Dir, "projects", "proj");
        Directory.CreateDirectory(root);
        return Path.GetDirectoryName(root)!;
    }

    [Fact]
    public async Task Prices_tokens_at_list_rates()
    {
        using var d = new TempData();
        var root = Root(d);
        // Opus 5.5: $4 in, $20 out, $0.20 cache read, 5m write 1.25x, 1h write 2x → 4 + 20 + 0.2 + 5 + 8 = $37.20
        File.WriteAllText(Path.Combine(root, "proj", "a.jsonl"),
            Line("1", "claude-opus-5-5", Now.AddHours(-1), input: 1_000_000, output: 1_000_000, read: 1_000_000, w5: 1_000_000, w1h: 1_000_000));
        var c = await CostScan.RunAsync([root], Now);
        Assert.Equal(37.20, c.Last30, 2);
        Assert.Equal(37.20, c.Today, 2);
        Assert.Equal("Opus 5.5", c.ByModelLast30.Single().Model);
    }

    [Fact]
    public async Task Counts_each_response_once_across_lines_and_files()
    {
        using var d = new TempData();
        var root = Root(d);
        var dup = Line("1", "claude-sonnet-5-5", Now.AddHours(-1), output: 1_000_000); // $10
        // Same response written once per content block, then copied into a resumed session file.
        File.WriteAllText(Path.Combine(root, "proj", "a.jsonl"), dup + dup + dup);
        File.WriteAllText(Path.Combine(root, "proj", "b.jsonl"), dup + Line("2", "claude-sonnet-5-5", Now.AddHours(-1), output: 100_000));
        var c = await CostScan.RunAsync([root], Now);
        Assert.Equal(11.0, c.Last30, 2);
        Assert.Equal(2, c.RequestsLast30);
    }

    [Fact]
    public async Task Reads_appended_lines_incrementally_and_keeps_totals()
    {
        using var d = new TempData();
        var root = Root(d);
        var f = Path.Combine(root, "proj", "a.jsonl");
        File.WriteAllText(f, Line("1", "claude-haiku-4-5", Now.AddDays(-2), output: 1_000_000)); // $5
        Assert.Equal(5.0, (await CostScan.RunAsync([root], Now)).Last30, 2);

        // A partial line (still being written) is not counted until it is complete.
        var next = Line("2", "claude-haiku-4-5", Now.AddHours(-1), output: 1_000_000);
        File.AppendAllText(f, next[..40]);
        Assert.Equal(5.0, (await CostScan.RunAsync([root], Now)).Last30, 2);
        File.AppendAllText(f, next[40..]);
        var c = await CostScan.RunAsync([root], Now);
        Assert.Equal(10.0, c.Last30, 2);
        Assert.Equal(5.0, c.Today, 2);

        // Claude Code deletes old logs: the totals stay.
        File.Delete(f);
        Assert.Equal(10.0, (await CostScan.RunAsync([root], Now)).Last30, 2);
    }

    [Fact]
    public async Task Fast_mode_doubles_and_unknown_models_are_listed()
    {
        using var d = new TempData();
        var root = Root(d);
        File.WriteAllText(Path.Combine(root, "proj", "a.jsonl"),
            Line("1", "claude-opus-5-5", Now.AddHours(-1), output: 1_000_000, speed: "fast") + // $40
            Line("2", "some-future-model", Now.AddHours(-1), output: 1_000_000) +
            "{\"type\":\"user\",\"message\":{\"content\":\"not usage\"}}\n" +
            "not json at all \"usage\"\n");
        var c = await CostScan.RunAsync([root], Now);
        Assert.Equal(40.0, c.Last30, 2);
        Assert.Contains("some-future-model", c.UnpricedModels);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4.0)]
    [InlineData("claude-opus-4-1-20250805", 15.0)]
    [InlineData("claude-sonnet-4-6", 3.0)]
    [InlineData("anthropic.claude-haiku-4-5", 1.0)]
    [InlineData("claude-fable-5-1", 10.0)]
    public void Longest_prefix_wins(string model, double input) => Assert.Equal(input, Pricing.Find(model)!.Input);
}
