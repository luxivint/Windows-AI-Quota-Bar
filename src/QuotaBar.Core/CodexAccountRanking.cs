using CodexAccountSwitcher.Core;

namespace QuotaBar.Core;

/// <summary>Comparison of displayed quotas only; never switches or rotates accounts.</summary>
public static class CodexAccountRanking
{
    public static int? Score(AccountRow row) => row.UsageStatus == "loaded" && row.Usage is { FiveHourRemainingPercent: { } five } usage
        ? Math.Min(five, usage.RemainingPercent) : null;

    public static Guid? Recommend(IEnumerable<AccountRow> rows) => rows.Where(r => Score(r) is > 0)
        .OrderByDescending(Score).ThenByDescending(r => r.Usage!.RemainingPercent)
        .ThenBy(r => r.Profile.Email ?? r.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(r => (Guid?)r.Profile.Id).FirstOrDefault();

    public static IEnumerable<AccountRow> Order(IEnumerable<AccountRow> rows, string query, string sort)
    {
        rows = rows.Where(r => string.IsNullOrWhiteSpace(query)
            || (r.Profile.Email ?? "").Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || r.Profile.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        return sort switch
        {
            "weekly" => rows.OrderByDescending(r => r.Usage?.RemainingPercent ?? -1),
            "session" => rows.OrderByDescending(r => r.Usage?.FiveHourRemainingPercent ?? -1),
            "reset" => rows.OrderBy(r => r.Usage?.FiveHourResetsAt ?? DateTimeOffset.MaxValue),
            _ => rows.OrderByDescending(r => Score(r) ?? -1).ThenByDescending(r => r.Usage?.RemainingPercent ?? -1)
                .ThenBy(r => r.Profile.Email ?? r.Profile.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
    }
}
