using QuotaBar.Core.Providers;

namespace QuotaBar.Tests;

public class SettingsTransferTests
{
    static AppSettings Sample() => new()
    {
        RefreshMinutes = 7, WidgetStyle = "bars", WidgetColor = "status", Language = "de", WidgetOffset = 120,
        KeyAccounts = [new KeyAccount { Kind = "openrouter", Label = "Work key" }],
        OpenCodeExtraKeyLabels = ["Second"], LastSelectedProvider = "codex",
        Alerts = new AlertSettings { Thresholds = [70] },
    };

    [Fact]
    public void Export_leaves_out_keys_and_local_state()
    {
        var text = SettingsTransfer.Export(Sample());
        Assert.DoesNotContain("Work key", text);
        Assert.DoesNotContain("keyAccounts", text);
        Assert.DoesNotContain("openCodeExtraKeyLabels", text);
        Assert.DoesNotContain("widgetOffset", text);
        Assert.Contains("\"widgetColor\": \"status\"", text);
        Assert.Contains("Windows AI Quota Bar", text);
    }

    [Fact]
    public void Import_restores_settings_and_keeps_local_keys()
    {
        var text = SettingsTransfer.Export(Sample());
        var here = new AppSettings { KeyAccounts = [new KeyAccount { Kind = "deepseek", Label = "Mine" }], WidgetOffset = 5 };
        var s = SettingsTransfer.Import(text, here);
        Assert.Equal(7, s.RefreshMinutes);
        Assert.Equal("bars", s.WidgetStyle);
        Assert.Equal("de", s.Language);
        Assert.Equal([70], s.Alerts.Thresholds);
        Assert.Equal("Mine", s.KeyAccounts.Single().Label);
        Assert.Equal(5, s.WidgetOffset);
    }

    [Fact]
    public void Import_ignores_keys_smuggled_into_a_backup()
    {
        var text = """{"app":"Windows AI Quota Bar","format":1,"settings":{"refreshMinutes":500,"keyAccounts":[{"kind":"openrouter","label":"Injected"}]}}""";
        var s = SettingsTransfer.Import(text, new AppSettings());
        Assert.Empty(s.KeyAccounts);
        Assert.Equal(120, s.RefreshMinutes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"app":"Something else","settings":{}}""")]
    [InlineData("""{"hello":1}""")]
    public void Import_rejects_other_files(string text) =>
        Assert.Throws<FormatException>(() => SettingsTransfer.Import(text, new AppSettings()));
}
