using CodexAccountSwitcher.Core;
using QuotaBar.Core.Providers;

namespace QuotaBar.Tests;

public class CodexSnapshotTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static AccountSnapshot Snapshot(bool confirmed = true) => new(new[]
    {
        new AccountRow(new Profile(First, "First", "first@example.test"), "F", new Usage(72, Now.AddDays(3), 48, Now.AddHours(2), 2), null),
        new AccountRow(new Profile(Second, "Second", null), "S", new Usage(91, Now.AddDays(5), null, null), null),
    }, First, new Preferences(), false, false, confirmed, null, new());

    [Fact]
    public void AllAccountsAppearAndRemainingQuotaIsMappedToUsed()
    {
        var mapped = CodexSnapshotMapper.Map(Snapshot(), Now);
        Assert.Equal(2, mapped.Count);
        Assert.True(mapped[0].IsActiveAccount); Assert.False(mapped[1].IsActiveAccount);
        Assert.Equal("Second", mapped[1].Account);
        Assert.Equal(52, mapped[0].Windows.Single(w => w.Key == "primary").UsedPercent);
        Assert.Equal(28, mapped[0].Windows.Single(w => w.Key == "secondary").UsedPercent);
        Assert.Single(mapped[1].Windows);
    }

    [Fact]
    public void ProviderIdentityDoesNotChangeWhenAnotherAccountBecomesActive()
    {
        var state = Snapshot();
        var before = CodexSnapshotMapper.Map(state, Now);
        var after = CodexSnapshotMapper.Map(state with { ActiveAccountID = Second }, Now);
        Assert.Equal(before.Select(p => p.Id), after.Select(p => p.Id));
        Assert.False(after[0].IsActiveAccount); Assert.True(after[1].IsActiveAccount);
        Assert.NotEqual(before[0].Id, before[1].Id);
    }

    [Fact]
    public void UnconfirmedIdentityDoesNotMarkAnAccountActive()
        => Assert.All(CodexSnapshotMapper.Map(Snapshot(false), Now), p => Assert.False(p.IsActiveAccount));

    [Fact]
    public void FailedRefreshRetainsThatAccountsCachedUsageAndTimestamp()
    {
        var state = Snapshot();
        state.Accounts[0] = state.Accounts[0] with { UsageStatus = "stale", UsageError = "fixture failure" };
        var fetched = Now.AddHours(-1);
        var mapped = CodexSnapshotMapper.Map(state, Now, new Dictionary<Guid, DateTimeOffset> { [First] = fetched });
        Assert.Equal(ProviderStatus.Ok, mapped[0].Status);
        Assert.Equal(fetched, mapped[0].FetchedAt);
        Assert.Contains(mapped[0].Info, i => i.Label == "Cached");
        Assert.Equal(52, mapped[0].Windows[0].UsedPercent);
    }

    [Fact]
    public void MissingUsageDoesNotInventLimits()
    {
        var state = Snapshot(); state.Accounts[0] = state.Accounts[0] with { Usage = null, UsageStatus = "unavailable", UsageError = "fixture failure" };
        var mapped = CodexSnapshotMapper.Map(state, Now);
        Assert.Equal(ProviderStatus.Error, mapped[0].Status); Assert.Empty(mapped[0].Windows);
    }

    [Fact]
    public async Task ProviderUsesTheSharedSnapshotWithoutOpeningAnotherCodexClient()
    {
        var snapshots = CodexSnapshotMapper.Map(Snapshot(), Now);
        var provider = new CodexProvider(() => snapshots);
        Assert.Same(snapshots, await provider.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public void SwitchingBetweenAccountsDoesNotTriggerAFalseResetAlert()
    {
        var state = Snapshot();
        var previous = CodexSnapshotMapper.Map(state, Now);
        var changed = state with { ActiveAccountID = Second };
        changed.Accounts[1] = changed.Accounts[1] with { Usage = new Usage(100, Now.AddDays(5), null, null) };
        var current = CodexSnapshotMapper.Map(changed, Now);
        var alerts = Alerts.Evaluate(Make.Report(previous[0]), Make.Report(current[1]), new AlertSettings(), new AlertState(), Now);
        Assert.DoesNotContain(alerts, a => a.Kind == AlertKind.Reset);
    }

    [Fact]
    public void RecommendationBalancesBothLimitsAndExcludesStaleOrMissingUsage()
    {
        var good = Snapshot().Accounts[0];
        var blocked = good with { Profile = new Profile(Guid.NewGuid(), "Blocked", null), Usage = new Usage(100, Now.AddDays(5), 0, Now.AddHours(1)) };
        var stale = good with { Profile = new Profile(Guid.NewGuid(), "Stale", null), Usage = new Usage(100, Now.AddDays(5), 100, Now.AddHours(1)), UsageStatus = "stale" };
        var unknown = good with { Profile = new Profile(Guid.NewGuid(), "Unknown", null), Usage = new Usage(100, Now.AddDays(5), null, null) };
        Assert.Equal(good.Profile.Id, CodexAccountRanking.Recommend(new[] { blocked, stale, unknown, good }));
        Assert.Null(CodexAccountRanking.Recommend(new[] { blocked, stale, unknown }));
    }

    [Fact]
    public void ThirtyAccountsCanBeSearchedAndSortedWithoutLosingProfiles()
    {
        var prototype = Snapshot().Accounts[0];
        var rows = Enumerable.Range(1, 30).Select(i => prototype with {
            Profile = new Profile(Guid.NewGuid(), $"Account {i}", $"account{i:00}@example.test"),
            Usage = new Usage(i, Now.AddDays(3), 100 - i, Now.AddMinutes(i)) }).ToArray();
        Assert.Equal(30, CodexAccountRanking.Order(rows, "", "weekly").Count());
        Assert.Equal(rows[29].Profile.Id, CodexAccountRanking.Order(rows, "", "weekly").First().Profile.Id);
        Assert.Equal(rows[0].Profile.Id, CodexAccountRanking.Order(rows, "", "reset").First().Profile.Id);
        Assert.Equal(rows[29].Profile.Id, Assert.Single(CodexAccountRanking.Order(rows, "ACCOUNT30", "recommended")).Profile.Id);
    }
}
