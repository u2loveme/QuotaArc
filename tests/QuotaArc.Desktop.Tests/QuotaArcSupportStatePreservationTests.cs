using System.Text.Json;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaArcSupportStatePreservationTests
{
    [Fact]
    public void ExistingSupportLinkAndOneTimePromptStateSurviveStoreReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "QuotaArcSupportStateTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "support-engagement-state.json");
        var expected = new SupportEngagementState(
            DateTimeOffset.UtcNow.AddDays(-4),
            SuccessfulLaunchCount: 10,
            HasObservedLiveQuotaData: true,
            SupportPromptShown: false,
            SupportLinkOpened: true);

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(expected));

            var reloaded = new SupportEngagementStore(path).State;

            Assert.Equal(expected.SupportPromptShown, reloaded.SupportPromptShown);
            Assert.Equal(expected.SupportLinkOpened, reloaded.SupportLinkOpened);
            Assert.Equal(expected.SuccessfulLaunchCount, reloaded.SuccessfulLaunchCount);
            Assert.Equal(expected.FirstSuccessfulLaunchUtc, reloaded.FirstSuccessfulLaunchUtc);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
