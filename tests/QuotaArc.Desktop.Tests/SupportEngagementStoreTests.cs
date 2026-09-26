using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class SupportEngagementStoreTests
{
    private static readonly SupportPromptConditions HealthyConditions = new(
        HealthyLiveState: true,
        MainWindowActive: true,
        SessionReady: true,
        SessionStartedAfterMinimumAge: true,
        HistoryReady: true,
        ConflictingSurfaceOpen: false);

    [Fact]
    public void NewStateIsNotEligible()
    {
        Assert.False(SupportPromptEligibility.IsEligible(
            new SupportEngagementState(), DateTimeOffset.UtcNow, HealthyConditions));
    }

    [Theory]
    [InlineData(23, 3, true, false, false)]
    [InlineData(24, 2, true, false, false)]
    [InlineData(24, 3, false, false, false)]
    [InlineData(24, 3, true, true, false)]
    [InlineData(24, 3, true, false, true)]
    public void PromptRequiresEveryHistoryAndOneShotGate(
        int hoursSinceFirstLaunch,
        int launchCount,
        bool observedLiveData,
        bool alreadyShown,
        bool linkAlreadyOpened)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SupportEngagementState(
            now.AddHours(-hoursSinceFirstLaunch), launchCount,
            observedLiveData, alreadyShown, linkAlreadyOpened);

        Assert.False(SupportPromptEligibility.IsEligible(state, now, HealthyConditions));
    }

    [Fact]
    public void PromptIsEligibleOnlyAfterAFullHealthySession()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SupportEngagementState(now.AddHours(-24), 3, true);

        Assert.True(SupportPromptEligibility.IsEligible(state, now, HealthyConditions));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { HealthyLiveState = false }));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { MainWindowActive = false }));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { SessionReady = false }));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { SessionStartedAfterMinimumAge = false }));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { HistoryReady = false }));
        Assert.False(SupportPromptEligibility.IsEligible(
            state, now, HealthyConditions with { ConflictingSurfaceOpen = true }));
    }

    [Fact]
    public void EngagementStatePersistsAcrossStoreInstances()
    {
        var folder = Path.Combine(Path.GetTempPath(), "QuotaArcSupportTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "state.json");
        try
        {
            var firstLaunch = DateTimeOffset.UtcNow.AddDays(-2);
            var store = new SupportEngagementStore(path);
            store.RecordSuccessfulLaunch(firstLaunch);
            store.RecordSuccessfulLaunch(firstLaunch.AddHours(1));
            store.RecordSuccessfulLaunch(firstLaunch.AddHours(2));
            store.RecordLiveQuotaData();

            var reloaded = new SupportEngagementStore(path);
            Assert.Equal(firstLaunch, reloaded.State.FirstSuccessfulLaunchUtc);
            Assert.Equal(3, reloaded.State.SuccessfulLaunchCount);
            Assert.True(reloaded.State.HasObservedLiveQuotaData);

            reloaded.RecordPromptShown();
            reloaded.RecordSupportLinkOpened();
            var afterUpdate = new SupportEngagementStore(path);
            Assert.True(afterUpdate.State.SupportPromptShown);
            Assert.True(afterUpdate.State.SupportLinkOpened);
            Assert.False(SupportPromptEligibility.IsEligible(
                afterUpdate.State, DateTimeOffset.UtcNow, HealthyConditions));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RepeatedLiveObservationsAndOneShotActionsRemainIdempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "QuotaArcSupportTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SupportEngagementStore(Path.Combine(folder, "state.json"));
            store.RecordLiveQuotaData();
            store.RecordLiveQuotaData();
            store.RecordPromptShown();
            store.RecordPromptShown();
            store.RecordSupportLinkOpened();
            store.RecordSupportLinkOpened();

            Assert.True(store.State.HasObservedLiveQuotaData);
            Assert.True(store.State.SupportPromptShown);
            Assert.True(store.State.SupportLinkOpened);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
