namespace QuotaBar.Tests;

public class ServiceStatusTests
{
    [Theory]
    [InlineData("none", Health.Operational)]
    [InlineData("minor", Health.Minor)]
    [InlineData("major", Health.Major)]
    [InlineData("critical", Health.Critical)]
    public void Statuspage_indicator(string indicator, Health expected)
    {
        var json = "{\"page\":{\"id\":\"x\"},\"status\":{\"indicator\":\"" + indicator + "\",\"description\":\"Something\"}}";
        var h = ServiceStatus.ParseStatuspage("claude", json, "https://status.claude.com");
        Assert.Equal(expected, h.Health);
        Assert.Equal("Something", h.Description);
    }

    const string Gemini = "npdyhgECDJ6tB66MxXyo";

    [Fact]
    public void Google_only_ongoing_incidents_for_the_product()
    {
        var json = $$"""
        [
          {"end":"2026-09-25T16:40:00+00:00","status_impact":"SERVICE_OUTAGE","severity":"high","external_desc":"**Summary:**\nOld one","affected_products":[{"id":"{{Gemini}}"}]},
          {"status_impact":"SERVICE_OUTAGE","severity":"high","external_desc":"**Summary:**\nOther product","affected_products":[{"id":"other"}]},
          {"status_impact":"SERVICE_DISRUPTION","severity":"medium","external_desc":"**Summary:**\nSlow responses in Gemini.\n**Description:** ...","affected_products":[{"id":"{{Gemini}}"}]}
        ]
        """;
        var h = ServiceStatus.ParseGoogle("antigravity", json, Gemini, "https://www.google.com/appsstatus/dashboard/");
        Assert.Equal(Health.Minor, h.Health);
        Assert.Equal("Slow responses in Gemini.", h.Description);
    }

    [Fact]
    public void Google_no_incident_is_operational()
    {
        var h = ServiceStatus.ParseGoogle("antigravity", "[]", Gemini, "x");
        Assert.Equal(Health.Operational, h.Health);
    }
}
