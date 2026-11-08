using CodexAccountSwitcher.Core;

namespace QuotaBar.Core;

/// <summary>Presentation mapping only. Account policy and quota normalization belong to upstream SwitcherCore.</summary>
public static class CodexSnapshotMapper
{
    public static string ProviderId(Guid id) => "codex:" + id.ToString("D");
    public static IReadOnlyList<ProviderSnapshot> Map(AccountSnapshot state, DateTimeOffset now,
        IReadOnlyDictionary<Guid, DateTimeOffset>? fetchedAt = null)
    {
        if (state.Accounts.Length == 0)
            return new[] { ProviderSnapshot.Fail("codex", "codex", "Codex",
                state.Error == null ? ProviderStatus.NeedsLogin : ProviderStatus.Error,
                state.Error ?? "Add or register an account in Codex accounts.") };
        return state.Accounts.Select(row =>
        {
            var result = new ProviderSnapshot
            {
                Id = ProviderId(row.Profile.Id), ProviderKind = "codex", DisplayName = "Codex",
                Account = row.Profile.Email ?? row.Profile.DisplayName,
                IsActiveAccount = row.Profile.Id == state.ActiveAccountID && state.ActiveIdentityConfirmed,
                Status = row.Usage == null ? ProviderStatus.Error : ProviderStatus.Ok,
                Message = row.UsageError ?? (row.Usage == null ? "Usage is loading or unavailable." : null),
                DashboardUrl = "https://chatgpt.com/codex/settings/usage", StatusUrl = "https://status.openai.com",
                FetchedAt = fetchedAt != null && fetchedAt.TryGetValue(row.Profile.Id, out var time)
                    ? time : row.UsageStatus == "stale" ? DateTimeOffset.MinValue : now,
            };
            if (row.Usage is { } usage)
            {
                if (usage.FiveHourRemainingPercent is { } five && usage.FiveHourResetsAt is { } reset)
                    result.Windows.Add(new UsageWindow { Key = "primary", Label = "Session", UsedPercent = 100 - five,
                        ResetsAt = reset, WindowSeconds = 18000 });
                result.Windows.Add(new UsageWindow { Key = "secondary", Label = "Weekly", UsedPercent = 100 - usage.RemainingPercent,
                    ResetsAt = usage.ResetsAt, WindowSeconds = 604800 });
                if (usage.ResetCreditsRemaining is { } count) result.Info.Add(new("Available resets", count.ToString()));
            }
            if (row.Subscription is { } subscription)
            {
                result.Info.Add(new("Subscription snapshot", subscription.ActiveUntil.ToString("yyyy-MM-dd")));
                result.Info.Add(new("Snapshot checked", subscription.LastChecked.ToLocalTime().ToString("g")));
            }
            if (row.UsageStatus == "stale") result.Info.Add(new("Cached", row.UsageError ?? "Last successful reading"));
            return result;
        }).ToArray();
    }
}
