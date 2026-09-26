using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaFreshnessTests
{
    [Fact]
    public void MissingQuotaIsUnavailable()
    {
        Assert.Equal(QuotaSampleState.Unavailable, QuotaFreshness.GetState(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void QuotaAtTenMinuteBoundaryRemainsCurrent()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = SampledAt(now - TimeSpan.FromMinutes(10));

        Assert.Equal(QuotaSampleState.Current, QuotaFreshness.GetState(quota, now));
    }

    [Fact]
    public void QuotaOlderThanTenMinutesIsStale()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = SampledAt(now - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        Assert.Equal(QuotaSampleState.Stale, QuotaFreshness.GetState(quota, now));
    }

    [Fact]
    public void FutureQuotaSampleIsStale()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = SampledAt(now + TimeSpan.FromSeconds(1));

        Assert.Equal(QuotaSampleState.Stale, QuotaFreshness.GetState(quota, now));
    }

    [Fact]
    public void LiveStatusRequiresFreshSamplesFromLiveSource()
    {
        var now = DateTimeOffset.UtcNow;
        var status = QuotaFreshness.GetUpdateStatus(
            SampledAt(now - TimeSpan.FromSeconds(30)), now, allQuotasFromLiveSource: true,
            liveSourceAvailable: true);

        Assert.Equal("Live · Updated just now", status.Text);
        Assert.Equal(QuotaSampleState.Current, status.State);
    }

    [Fact]
    public void RecentStatusIsUsedForLocalOrOlderCurrentSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var localStatus = QuotaFreshness.GetUpdateStatus(
            SampledAt(now - TimeSpan.FromSeconds(30)), now, allQuotasFromLiveSource: false,
            liveSourceAvailable: true);
        var olderStatus = QuotaFreshness.GetUpdateStatus(
            SampledAt(now - TimeSpan.FromMinutes(3)), now, allQuotasFromLiveSource: true,
            liveSourceAvailable: true);

        Assert.Equal("Recent · Updated just now", localStatus.Text);
        Assert.Equal("Recent · Updated 3m", olderStatus.Text);
        Assert.Equal(QuotaSampleState.Current, olderStatus.State);
    }

    [Fact]
    public void StaleAndFutureSamplesDoNotClaimToBeLive()
    {
        var now = DateTimeOffset.UtcNow;
        var staleStatus = QuotaFreshness.GetUpdateStatus(
            SampledAt(now - TimeSpan.FromMinutes(11)), now, allQuotasFromLiveSource: true,
            liveSourceAvailable: true);
        var futureStatus = QuotaFreshness.GetUpdateStatus(
            SampledAt(now + TimeSpan.FromSeconds(1)), now, allQuotasFromLiveSource: true,
            liveSourceAvailable: true);

        Assert.Equal("Stale · Updated 11m", staleStatus.Text);
        Assert.Equal("Clock mismatch · Sample time ahead", futureStatus.Text);
        Assert.Equal(QuotaSampleState.Stale, staleStatus.State);
        Assert.Equal(QuotaSampleState.Stale, futureStatus.State);
    }

    [Fact]
    public void OfflineStatusKeepsAuthoritativeSampleAge()
    {
        var now = DateTimeOffset.UtcNow;
        var status = QuotaFreshness.GetUpdateStatus(
            SampledAt(now - TimeSpan.FromMinutes(12)), now, allQuotasFromLiveSource: true,
            liveSourceAvailable: false);

        Assert.Equal("Offline · Stale · Updated 12m", status.Text);
        Assert.Equal(QuotaSampleState.Offline, status.State);
        Assert.Equal("Offline · Stale · Last update 12m ago",
            QuotaFreshness.FormatSampleDetail(SampledAt(now - TimeSpan.FromMinutes(12)), now, false));
    }

    [Fact]
    public void MissingQuotaDistinguishesOfflineFromUnavailable()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(new QuotaUpdateStatus("Unavailable · No quota sample", QuotaSampleState.Unavailable),
            QuotaFreshness.GetUpdateStatus(null, now, false, liveSourceAvailable: true));
        Assert.Equal(new QuotaUpdateStatus("Offline · No quota sample", QuotaSampleState.Offline),
            QuotaFreshness.GetUpdateStatus(null, now, false, liveSourceAvailable: false));
    }

    [Fact]
    public void FutureSampleIsDescribedAsClockMismatchInCompactDetails()
    {
        var now = DateTimeOffset.UtcNow;
        var future = SampledAt(now + TimeSpan.FromSeconds(30));

        Assert.Equal("Clock mismatch · sample time is ahead",
            QuotaFreshness.FormatSampleDetail(future, now, liveSourceAvailable: true));
        Assert.Equal("Offline · Clock mismatch · sample time is ahead",
            QuotaFreshness.FormatSampleDetail(future, now, liveSourceAvailable: false));
    }

    private static QuotaUsageSnapshot SampledAt(DateTimeOffset sampledAt) => new(
        "5-hour", "codex", 16, 300, sampledAt, null, "APP_SERVER_LIVE");
}
