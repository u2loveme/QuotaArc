using System.Globalization;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class CompactWindowPresentationTests
{
    [Fact]
    public void UnavailableQuotaUsesDashInsteadOfZeroPercent()
    {
        Assert.Equal("—", CompactWindow.FormatRemaining(null));
    }

    [Fact]
    public void RemainingQuotaIsFormattedAsPercentage()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var quota = new QuotaUsageSnapshot("5-hour", "codex", 15.5, 300,
                DateTimeOffset.UtcNow, null, "APP_SERVER_LIVE");

            Assert.Equal("84.5%", CompactWindow.FormatRemaining(quota));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ActualZeroRemainingQuotaIsDisplayedAsZeroNotUnavailable()
    {
        var quota = new QuotaUsageSnapshot("5-hour", "codex", 100, 300,
            DateTimeOffset.UtcNow, null, "APP_SERVER_LIVE");

        Assert.Equal("0%", CompactWindow.FormatRemaining(quota));
        Assert.NotEqual("—", CompactWindow.FormatRemaining(quota));
    }

    [Theory]
    [InlineData(-20, "100%")]
    [InlineData(125, "0%")]
    public void RemainingQuotaIsClampedToVisibleRange(double used, string expected)
    {
        var quota = new QuotaUsageSnapshot("5-hour", "codex", used, 300,
            DateTimeOffset.UtcNow, null, "APP_SERVER_LIVE");

        Assert.Equal(expected, CompactWindow.FormatRemaining(quota));
    }
}
