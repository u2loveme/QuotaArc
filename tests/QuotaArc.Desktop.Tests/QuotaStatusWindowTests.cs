using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaStatusWindowTests
{
    [Fact]
    public void HeaderRendersLiveStaleOfflineAndUnavailableStatesFromInMemorySamples()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderAndAssertStates();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void RenderAndAssertStates()
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        window.Width = 1180;
        window.Height = 800;
        var loaded = (RoutedEventHandler)Delegate.CreateDelegate(
            typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        window.Loaded -= loaded;
        window.Left = -5000;
        window.Top = -5000;
        window.Show();

        var now = DateTimeOffset.UtcNow;
        var fresh = Sample(now - TimeSpan.FromSeconds(30));
        AssertState(window, fresh, isOffline: false, "Live · Updated just now", "live");

        var stale = Sample(now - TimeSpan.FromMinutes(12));
        AssertState(window, stale, isOffline: false, "Stale · Updated 12m", "stale");
        AssertState(window, stale, isOffline: true, "Offline · Stale · Updated 12m", "offline");
        AssertState(window, null, isOffline: false, "Unavailable · No quota sample", "unavailable");

        // Hiding avoids the production Closed handler, which persists window position.
        window.Hide();
    }

    private static void AssertState(
        MainWindow window,
        QuotaUsageSnapshot? sample,
        bool isOffline,
        string expectedText,
        string imageName)
    {
        typeof(MainWindow).GetField("_quotaSourceUnavailable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, isOffline);
        var snapshot = new DashboardUsageSnapshot(
            new TodayUsageSnapshot(DateOnly.FromDateTime(DateTime.Today), 0, 0, 0, 0, 0, 0, "3.53"),
            Array.Empty<DailyUsageSnapshot>(), sample,
            sample is null ? null : sample with { Kind = "weekly", WindowMinutes = 10080 },
            sample is null ? null : sample with { Kind = "reserve", LimitId = "base_model_inference", WindowMinutes = 10080 },
            Array.Empty<QuotaHistoryPoint>(), Array.Empty<QuotaResetEventSnapshot>(),
            DateTimeOffset.UtcNow, 14, 24);
        typeof(MainWindow).GetMethod("ShowDashboard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [snapshot, false]);

        var text = (System.Windows.Controls.TextBlock)typeof(MainWindow)
            .GetField("UpdatedAtText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var indicator = (System.Windows.Shapes.Ellipse)typeof(MainWindow)
            .GetField("UpdatedAtIndicator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var status = typeof(MainWindow)
            .GetField("UpdatedAtStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as FrameworkElement;

        Assert.Equal(expectedText, text.Text);
        Assert.Equal(Visibility.Visible, status!.Visibility);
        Assert.NotNull(indicator.Fill);

        var compact = new CompactWindow(window);
        compact.UpdateValues(snapshot, isOffline);
        var miniRow = (FrameworkElement)typeof(CompactWindow)
            .GetField("FiveHourRow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compact)!;
        Assert.Contains(sample is null
            ? isOffline ? "offline" : "unavailable"
            : QuotaFreshness.FormatSampleDetail(sample, snapshot.ReadAt, !isOffline),
            (miniRow.ToolTip as string)!, StringComparison.OrdinalIgnoreCase);

        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1180, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var path = Path.Combine(Path.GetTempPath(), $"quotaarc-quota-status-{imageName}.png");
        using var stream = File.Create(path);
        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
    }

    private static QuotaUsageSnapshot Sample(DateTimeOffset sampledAt) =>
        new("5-hour", "codex", 16, 300, sampledAt, null, "APP_SERVER_LIVE");
}
