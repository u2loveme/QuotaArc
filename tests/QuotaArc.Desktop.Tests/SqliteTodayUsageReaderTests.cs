using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Data.Sqlite;
using System.Windows.Media;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class SqliteTodayUsageReaderTests
{
    [Fact]
    public void NotificationActivationArgumentsRouteTestAndAlertContexts()
    {
        var test = App.ParseNotificationActivation("test=1");
        var alert = App.ParseNotificationActivation("alertId=42");
        var invalid = App.ParseNotificationActivation("alertId=not-a-number");

        Assert.True(test.IsTest);
        Assert.Null(test.AlertId);
        Assert.False(alert.IsTest);
        Assert.Equal(42L, alert.AlertId);
        Assert.False(invalid.IsTest);
        Assert.Null(invalid.AlertId);
    }

    [Fact]
    public void SecondSingleInstanceCoordinatorActivatesTheFirst()
    {
        var directory = CreateTemporaryDirectory();
        var mutexName = $@"Local\QuotaArc.Tests.{Guid.NewGuid():N}";
        try
        {
            using var activated = new ManualResetEventSlim();
            using var first = new DesktopSingleInstanceCoordinator(mutexName, directory);
            using var second = new DesktopSingleInstanceCoordinator(mutexName, directory);

            Assert.True(first.TryAcquire(() => activated.Set()));
            Assert.False(second.TryAcquire(() => { }));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NotificationFeedGroupsRepeatedQuotaEventsAndKeepsUnreadState()
    {
        var now = DateTimeOffset.UtcNow;
        AlertEventSnapshot Event(string kind, string eventType, string message, int minutesAgo, bool unread) => new(
            minutesAgo,
            kind,
            eventType,
            now.AddMinutes(-minutesAgo),
            message,
            Colors.Orange,
            Brushes.Orange,
            unread);

        var feed = MainWindow.GroupAlertEvents(
        [
            Event("5-hour", "LOW_THRESHOLD", "5-hour: 25% remaining", 10, false),
            Event("5-hour", "LOW_THRESHOLD", "5-hour: 10% remaining", 2, true),
            Event("weekly", "RESET", "weekly quota refreshed", 5, false)
        ]);

        Assert.Equal(2, feed.Count);
        Assert.Equal(2, feed[0].Count);
        Assert.True(feed[0].IsUnread);
        Assert.Equal("5-hour: 10% remaining", feed[0].Message);
        Assert.Equal("5-HOUR · LOW QUOTA", feed[0].Category);
        Assert.Equal(1, feed[1].Count);
    }

    [Fact]
    public void NotificationPreferencesDefaultQuietlyAndPersistIndependently()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            CreateDatabase(path, DateOnly.FromDateTime(DateTime.Now));
            var reader = new SqliteTodayUsageReader();

            Assert.Equal("quiet", reader.ReadNotificationDeliveryMode(path));
            Assert.Equal("dot", reader.ReadNotificationBadgeMode(path));
            Assert.Equal("off", reader.ReadNotificationSoundMode(path));

            reader.SaveNotificationDeliveryMode("banner", path);
            reader.SaveNotificationBadgeMode("count", path);
            reader.SaveNotificationSoundMode("important", path);

            Assert.Equal("banner", reader.ReadNotificationDeliveryMode(path));
            Assert.Equal("count", reader.ReadNotificationBadgeMode(path));
            Assert.Equal("important", reader.ReadNotificationSoundMode(path));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.SaveNotificationDeliveryMode("loud", path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NotificationPreferencesRespectLegacyOffAndSoundSettings()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            CreateDatabase(path, DateOnly.FromDateTime(DateTime.Now));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO app_settings(key, value) VALUES ('desktop_notifications_enabled', '0');
                    INSERT INTO app_settings(key, value) VALUES ('desktop_notification_sound_enabled', '1');
                    """;
                command.ExecuteNonQuery();
            }

            var reader = new SqliteTodayUsageReader();
            Assert.Equal("off", reader.ReadNotificationDeliveryMode(path));
            Assert.Equal("all", reader.ReadNotificationSoundMode(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void QuotaChartDownsamplingPreservesBucketExtremesWithinPointLimit()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var points = Enumerable.Range(0, 1200)
            .Select(index => new QuotaHistoryPoint(
                "5-hour",
                start.AddSeconds(index),
                (index % 4) switch
                {
                    1 => 20,
                    3 => 80,
                    _ => 50
                }))
            .ToArray();

        var reduced = MainWindow.Downsample(points, 600);

        Assert.Equal(600, reduced.Count);
        Assert.Equal(300, reduced.Count(point => point.RemainingPercent == 20));
        Assert.Equal(300, reduced.Count(point => point.RemainingPercent == 80));
        Assert.True(reduced.Zip(reduced.Skip(1)).All(pair => pair.First.SampledAt <= pair.Second.SampledAt));
    }

    [Fact]
    public void QuotaChartBreaksRealDataLinesAcrossMissingIntervals()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        QuotaHistoryPoint[] points =
        [
            new("5-hour", start, 80),
            new("5-hour", start.AddSeconds(30), 79),
            new("5-hour", start.AddMinutes(8), 77),
            new("5-hour", start.AddMinutes(8).AddSeconds(30), 76)
        ];

        var segments = MainWindow.SplitQuotaHistoryAtGaps(points, TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { 2, 2 }, segments.Select(segment => segment.Count));
        Assert.Equal(points[1], segments[0][^1]);
        Assert.Equal(points[2], segments[1][0]);

        var bridges = MainWindow.FindQuotaGapBridges(points, TimeSpan.FromMinutes(5));
        Assert.Single(bridges);
        Assert.Equal(points[1], bridges[0].From);
        Assert.Equal(points[2], bridges[0].To);
    }

    [Fact]
    public void LongRangeQuotaBarsAggregateObservedUseByConfirmedResetBoundary()
    {
        var start = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        QuotaHistoryPoint[] points =
        [
            new("5-hour", start, 70, start.AddHours(5)),
            new("5-hour", start.AddHours(4), 20, start.AddHours(5)),
            new("5-hour", start.AddHours(5).AddMinutes(1), 100, start.AddHours(10).AddMinutes(1)),
            new("5-hour", start.AddHours(9), 65, start.AddHours(10).AddMinutes(1))
        ];

        var bars = MainWindow.BuildQuotaCycleBars(points);

        Assert.Equal(2, bars.Count);
        Assert.Equal(80, bars[0].UsedPercent);
        Assert.Equal(35, bars[1].UsedPercent);
        Assert.Equal(2, bars[0].SampleCount);
        Assert.Equal(2, bars[1].SampleCount);
        Assert.Equal(points[0].SampledAt, bars[0].FirstSampleAt);
        Assert.Equal(points[1].SampledAt, bars[0].LastSampleAt);

        var tooltip = MainWindow.FormatQuotaCycleBarTooltip(bars[0], CultureInfo.InvariantCulture);
        Assert.Contains("5-hour cycle · 80% used", tooltip);
        Assert.Contains("Observed samples: 2", tooltip);
        Assert.Contains(points[0].SampledAt.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.InvariantCulture), tooltip);
        Assert.Contains(points[1].SampledAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), tooltip);
    }

    [Fact]
    public void LongRangeQuotaBarsDoNotInventSingleSampleCyclesOrNonFiveHourSeries()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        QuotaHistoryPoint[] points =
        [
            new("weekly", start, 80, start.AddDays(7)),
            new("5-hour", start, 30, start.AddHours(5))
        ];

        Assert.Empty(MainWindow.BuildQuotaCycleBars(points));
    }

    [Fact]
    public void QuotaChartInspectionSnapsToAnExistingSampleWithoutInterpolating()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        QuotaHistoryPoint[] points =
        [
            new("5-hour", start, 80),
            new("5-hour", start.AddMinutes(2), 70)
        ];

        var snapped = MainWindow.FindNearestPoint(points, start.AddMinutes(1));

        Assert.Equal(points[0], snapped);
        Assert.Contains(snapped, points);
    }

    [Theory]
    [InlineData(168)]
    [InlineData(336)]
    [InlineData(720)]
    public void LongRangeQuotaInspectionUsesOnlyTheRenderedWeeklySeries(int quotaHours)
    {
        var start = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        QuotaHistoryPoint[] fiveHour = [new("5-hour", start, 11)];
        QuotaHistoryPoint[] weekly = [new("weekly", start.AddMinutes(1), 40)];

        var series = MainWindow.GetQuotaInspectionSeries(quotaHours, fiveHour, weekly);

        Assert.Same(weekly, series);
        Assert.DoesNotContain(series, point => point.Kind == "5-hour");
    }

    [Fact]
    public void ShortRangeQuotaInspectionStillIncludesBothRenderedLineSeries()
    {
        var start = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        QuotaHistoryPoint[] fiveHour = [new("5-hour", start, 91)];
        QuotaHistoryPoint[] weekly = [new("weekly", start.AddMinutes(1), 40)];

        var series = MainWindow.GetQuotaInspectionSeries(24, fiveHour, weekly);

        Assert.Equal(new[] { "5-hour", "weekly" }, series.Select(point => point.Kind));
    }

    [Fact]
    public void QuotaChartInspectionPreservesExactSamplePercentage()
    {
        Assert.Equal("68.125", MainWindow.FormatQuotaPercent(68.125, CultureInfo.InvariantCulture));
        Assert.Equal("68", MainWindow.FormatQuotaPercent(68, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void QuotaInspectionTooltipStaysInsideChartAtOppositeEdges()
    {
        var chartSize = new Size(320, 160);
        var tooltipSize = new Size(140, 60);

        var topLeft = MainWindow.PlaceQuotaInspectionTooltip(
            new Point(20, 15), tooltipSize, chartSize);
        var bottomRight = MainWindow.PlaceQuotaInspectionTooltip(
            new Point(300, 150), tooltipSize, chartSize);

        Assert.Equal(new Point(32, 27), topLeft);
        Assert.Equal(new Point(148, 78), bottomRight);
        Assert.InRange(topLeft.X, 6, chartSize.Width - tooltipSize.Width - 6);
        Assert.InRange(topLeft.Y, 6, chartSize.Height - tooltipSize.Height - 6);
        Assert.InRange(bottomRight.X, 6, chartSize.Width - tooltipSize.Width - 6);
        Assert.InRange(bottomRight.Y, 6, chartSize.Height - tooltipSize.Height - 6);
    }

    [Fact]
    public void NotificationFlyoutOffersRightAndLeftPlacementsAboveAndBelowAnchor()
    {
        var placements = MainWindow.PlaceNotificationPopup(
            new Size(360, 480),
            new Size(34, 32),
            new Point());

        Assert.Equal(4, placements.Length);
        Assert.Equal(new Point(-326, 38), placements[0].Point);
        Assert.Equal(new Point(-326, -486), placements[1].Point);
        Assert.Equal(new Point(0, 38), placements[2].Point);
        Assert.Equal(new Point(0, -486), placements[3].Point);
        Assert.All(placements, placement => Assert.Equal(PopupPrimaryAxis.Vertical, placement.PrimaryAxis));
    }

    [Fact]
    public void QuotaResetMarkersMergeNearSimultaneousKindsOnly()
    {
        var resetAt = DateTimeOffset.UtcNow;
        QuotaResetEventSnapshot[] events =
        [
            new(resetAt.AddSeconds(2), "weekly"),
            new(resetAt.AddSeconds(15), "5-hour"),
            new(resetAt, "5-hour"),
            new(resetAt.AddSeconds(3), "weekly")
        ];

        var groups = MainWindow.GroupQuotaResetEvents(events, TimeSpan.FromSeconds(5));

        Assert.Equal(2, groups.Count);
        Assert.Equal(resetAt, groups[0].EventAt);
        Assert.Equal(new[] { "5-hour", "weekly" }, groups[0].Kinds);
        Assert.Equal(new[] { "5-hour" }, groups[1].Kinds);
    }

    [Fact]
    public void ResetMarkersAreInferredFromConfirmedQuotaRefillsAndAvoidSmallJumps()
    {
        var before = DateTimeOffset.Parse("2026-09-25T10:59:00Z");
        var reset = DateTimeOffset.Parse("2026-09-25T11:00:00Z");
        QuotaHistoryPoint[] history =
        [
            new("5-hour", before, 65, reset, "APP_SERVER_LIVE"),
            new("5-hour", reset, 100, reset.AddHours(5), "APP_SERVER_LIVE"),
            new("weekly", before, 82, reset, "APP_SERVER_LIVE"),
            new("weekly", reset.AddMinutes(1), 90, reset.AddDays(7), "APP_SERVER_LIVE")
        ];
        QuotaResetEventSnapshot[] recorded = [new(reset.AddSeconds(2), "5-hour")];

        var reconciled = MainWindow.ReconcileQuotaResetEvents(history, recorded);

        var onlyReset = Assert.Single(reconciled);
        Assert.Equal("5-hour", onlyReset.Kind);
        Assert.Equal(reset, onlyReset.EventAt);
    }

    [Fact]
    public void ResetMarkersRetainRecordedEventsWhenSamplesCannotConfirmThem()
    {
        var reset = DateTimeOffset.Parse("2026-09-25T11:00:00Z");

        var reconciled = MainWindow.ReconcileQuotaResetEvents(
            [new("weekly", reset, 38)],
            [new(reset.AddMinutes(10), "weekly")]);

        Assert.Equal(new QuotaResetEventSnapshot(reset.AddMinutes(10), "weekly"), Assert.Single(reconciled));
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(12, 2)]
    [InlineData(24, 3)]
    public void DetailedQuotaRangesKeepResetEventsWithinTheirTimeWindow(int hours, int expectedCount)
    {
        var endAt = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        QuotaResetEventSnapshot[] events =
        [
            new(endAt.AddHours(-5), "5-hour"),
            new(endAt.AddHours(-11), "5-hour"),
            new(endAt.AddHours(-23), "weekly"),
            new(endAt.AddHours(-25), "5-hour")
        ];

        var visible = MainWindow.FilterQuotaResetEvents(events, endAt.AddHours(-hours), endAt);

        Assert.Equal(expectedCount, visible.Count);
    }

    [Fact]
    public void DailyChartKeepsEveryShortRangeValueLabelAndCompactsLongNumbers()
    {
        var full = MainWindow.FitDailyValueLabel(123_456_789, 100,
            text => text.Length * 5.0);
        var compact = MainWindow.FitDailyValueLabel(123_456_789, 40,
            text => text.Length * 5.0);
        var compactInteger = MainWindow.FitDailyValueLabel(123_456_789, 27,
            text => text.Length * 5.0);

        Assert.Equal(123_456_789.ToString("N0"), full);
        Assert.EndsWith("M", compact);
        Assert.EndsWith("M", compactInteger);
        Assert.True(compact.Length < full.Length);
        Assert.True(compactInteger.Length <= compact.Length);
        Assert.True(MainWindow.ShouldPlaceDailyValueInside(30, 14, 14));
        Assert.False(MainWindow.ShouldPlaceDailyValueInside(27, 14, 14));
        Assert.False(MainWindow.ShouldRenderQuotaStaleTail(11));
        Assert.True(MainWindow.ShouldRenderQuotaStaleTail(12));
    }

    [Fact]
    public void QuotaResetSummaryShowsRelativeTimeAndLocalCalendarDate()
    {
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        var today = MainWindow.ResetSummary(noon.AddHours(6), noon);
        var tomorrow = MainWindow.ResetSummary(noon.AddDays(1).AddHours(6), noon);
        var later = noon.AddDays(2).AddHours(1);
        var futureDate = MainWindow.ResetSummary(later, noon);

        Assert.Contains("Resets in 6h 0m", today);
        Assert.Contains("Today · 18:00", today);
        Assert.Contains("Tomorrow · 18:00", tomorrow);
        Assert.Contains(later.ToLocalTime().ToString("dd MMM", CultureInfo.CurrentCulture), futureDate);
        Assert.Equal("Reset time unavailable", MainWindow.ResetSummary(null, noon));
    }

    [Fact]
    public void ReadsLocalDayTotalsWithoutChangingDatabase()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            CreateDatabase(path, day);
            var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

            var dashboard = new SqliteTodayUsageReader().ReadDashboard(day, databasePath: path);
            var snapshot = dashboard.Today;

            Assert.Equal(3, snapshot.SampleCount);
            Assert.Equal(337, snapshot.Total);
            Assert.Equal(177, snapshot.Input);
            Assert.Equal(65, snapshot.Cached);
            Assert.Equal(95, snapshot.Output);
            Assert.Equal(65, snapshot.Reasoning);
            Assert.Equal(day, snapshot.LocalDay);
            Assert.NotEmpty(snapshot.SqliteVersion);
            Assert.Equal(14, dashboard.DailyHistoryDays);
            Assert.Equal(24, dashboard.QuotaHistoryHours);
            Assert.Equal(14, dashboard.History.Count);
            Assert.Equal(337, dashboard.History[^1].Total);
            Assert.NotNull(dashboard.FiveHour);
            Assert.NotNull(dashboard.Weekly);
            Assert.NotNull(dashboard.Reserve);
            Assert.Equal(84.0, 100 - dashboard.FiveHour.UsedPercent);
            Assert.Equal(39.0, 100 - dashboard.Weekly.UsedPercent);
            Assert.Equal(100.0, 100 - dashboard.Reserve.UsedPercent);
            Assert.Equal(3, dashboard.QuotaHistory.Count);
            Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "missing.sqlite3");

        try
        {
            Assert.Throws<FileNotFoundException>(
                () => new SqliteTodayUsageReader().ReadDashboard(DateOnly.FromDateTime(DateTime.Now), databasePath: path));
            Assert.Throws<FileNotFoundException>(
                () => new SqliteTodayUsageReader().SaveHistoryRangePreference("history_daily_range", 30, path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SummaryOnlyRefreshReadsCurrentMetricsWithoutHistory()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            CreateDatabase(path, day);

            var dashboard = new SqliteTodayUsageReader().ReadDashboard(
                day,
                databasePath: path,
                includeHistory: false);

            Assert.Equal(337, dashboard.Today.Total);
            Assert.NotNull(dashboard.FiveHour);
            Assert.NotNull(dashboard.Weekly);
            Assert.NotNull(dashboard.Reserve);
            Assert.Empty(dashboard.History);
            Assert.Empty(dashboard.QuotaHistory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavesHistoryRangePreferencesWithoutChangingTelemetry()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            CreateDatabase(path, day);
            var reader = new SqliteTodayUsageReader();

            reader.SaveHistoryRangePreference("history_daily_range", 30, path);
            reader.SaveHistoryRangePreference("history_quota_range", 336, path);

            var dashboard = reader.ReadDashboard(day, databasePath: path);
            Assert.Equal(30, dashboard.DailyHistoryDays);
            Assert.Equal(336, dashboard.QuotaHistoryHours);
            Assert.Equal(30, dashboard.History.Count);
            Assert.Equal(4, dashboard.QuotaHistory.Count);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value FROM app_settings WHERE key = 'history_daily_range'
                UNION ALL
                SELECT value FROM app_settings WHERE key = 'history_quota_range';
                """;
            using var result = command.ExecuteReader();
            Assert.True(result.Read());
            Assert.Equal("30", result.GetString(0));
            Assert.True(result.Read());
            Assert.Equal("336", result.GetString(0));
            Assert.False(result.Read());
            Assert.Equal(5, ScalarInt64(connection, "SELECT COUNT(*) FROM usage_samples;"));
            Assert.Equal(6, ScalarInt64(connection, "SELECT COUNT(*) FROM quota_samples;"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsExistingAlertEventsAndPersistsReadStateInSettings()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            CreateDatabase(path, DateOnly.FromDateTime(DateTime.Now));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('5-hour', 25, 'LOW_THRESHOLD', '2026-09-24T09:30:00+00:00',
                            '{"old_percent":30,"new_percent":24,"message":"5-hour: 24% remaining"}');
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('weekly', NULL, 'RESET', '2026-09-24T10:00:00+00:00',
                            '{"message":"weekly quota refreshed: 100% available"}');
                    """;
                command.ExecuteNonQuery();
            }

            var reader = new SqliteTodayUsageReader();
            var unread = reader.ReadRecentAlertEvents(path);
            Assert.Equal(2, unread.Count);
            Assert.Equal("weekly quota refreshed: 100% available", unread[0].Message);
            Assert.True(unread.All(alert => alert.IsUnread));
            Assert.Equal(new long[] { unread[1].Id, unread[0].Id },
                reader.ReadAlertEventsAfter(0, path).Select(alert => alert.Id));
            Assert.Empty(reader.ReadAlertEventsAfter(unread[0].Id, path));

            reader.MarkRecentAlertEventsRead(path);
            var read = reader.ReadRecentAlertEvents(path);

            Assert.All(read, alert => Assert.False(alert.IsUnread));
            Assert.Equal(5, ScalarInt64FromFile(path, "SELECT COUNT(*) FROM usage_samples;"));
            Assert.Equal(6, ScalarInt64FromFile(path, "SELECT COUNT(*) FROM quota_samples;"));
            Assert.True(reader.ReadDesktopNotificationsEnabled(path));
            reader.SaveDesktopNotificationsEnabled(false, path);
            Assert.False(reader.ReadDesktopNotificationsEnabled(path));
            Assert.False(reader.ReadDesktopNotificationSoundEnabled(path));
            reader.SaveDesktopNotificationSoundEnabled(true, path);
            Assert.True(reader.ReadDesktopNotificationSoundEnabled(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsOnlyAuthoritativeQuotaResetEventsWithinVisibleHistory()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            CreateDatabase(path, DateOnly.FromDateTime(DateTime.Now));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                var resetAt = DateTimeOffset.UtcNow.AddMinutes(-10);
                command.CommandText = """
                    DELETE FROM alert_events;
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('5-hour', NULL, 'RESET', $first, '{}');
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('weekly', NULL, 'RESET', $second, '{}');
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('weekly', 25, 'LOW_THRESHOLD', $third, '{}');
                    INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('reserve', NULL, 'RESET', $fourth, '{}');
                    """;
                command.Parameters.AddWithValue("$first", resetAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$second", resetAt.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$third", resetAt.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$fourth", resetAt.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            var resets = new SqliteTodayUsageReader().ReadDashboard(
                DateOnly.FromDateTime(DateTime.Now), quotaHours: 6, databasePath: path).QuotaResetEvents;

            Assert.Equal(new[] { "5-hour", "weekly" }, resets.Select(reset => reset.Kind));
            Assert.Equal(TimeSpan.FromSeconds(1), resets[1].EventAt - resets[0].EventAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidHistoryRangePreferencesWithoutWriting()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            CreateDatabase(path, DateOnly.FromDateTime(DateTime.Now));
            var reader = new SqliteTodayUsageReader();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                reader.SaveHistoryRangePreference("history_daily_range", 336, path));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                reader.SaveHistoryRangePreference("notifications", 1, path));

            var dashboard = reader.ReadDashboard(DateOnly.FromDateTime(DateTime.Now), databasePath: path);
            Assert.Equal(14, dashboard.DailyHistoryDays);
            Assert.Equal(24, dashboard.QuotaHistoryHours);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(336)]
    [InlineData(720)]
    public void SupportsLongQuotaHistoryRangesWithoutChangingDatabase(int hours)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "telemetry.sqlite3");

        try
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            CreateDatabase(path, day);

            var dashboard = new SqliteTodayUsageReader().ReadDashboard(day, quotaHours: hours, databasePath: path);

            Assert.Equal(hours, dashboard.QuotaHistoryHours);
            Assert.Equal(4, dashboard.QuotaHistory.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"QuotaArcTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateDatabase(string path, DateOnly day)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE usage_samples (
                identity TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                event_at TEXT NOT NULL,
                total_delta INTEGER NOT NULL,
                input_delta INTEGER NOT NULL,
                cached_delta INTEGER NOT NULL,
                output_delta INTEGER NOT NULL,
                reasoning_delta INTEGER NOT NULL,
                context_window INTEGER,
                cumulative_total INTEGER NOT NULL);
            CREATE TABLE quota_samples (
                id INTEGER PRIMARY KEY, kind TEXT NOT NULL, used_percent REAL NOT NULL,
                reset_at TEXT, window_minutes INTEGER, sampled_at TEXT NOT NULL,
                source TEXT NOT NULL, limit_id TEXT);
            CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE alert_events (
                id INTEGER PRIMARY KEY, kind TEXT NOT NULL, threshold REAL,
                event_type TEXT NOT NULL, event_at TEXT NOT NULL, detail TEXT NOT NULL);
            CREATE INDEX usage_by_day ON usage_samples(event_at);
            INSERT INTO app_settings VALUES ('history_daily_range', '14');
            INSERT INTO app_settings VALUES ('history_quota_range', '24');
            """;
        command.ExecuteNonQuery();

        InsertUsage(connection, "today-1", LocalNoon(day), 100, 60, 20, 30, 20);
        InsertUsage(connection, "today-2", LocalNoon(day).AddHours(1), 230, 110, 45, 65, 45);
        InsertUsage(connection, "today-start", LocalMidnightUtc(day), 7, 7, 0, 0, 0);
        InsertUsage(connection, "yesterday", LocalNoon(day.AddDays(-1)), 900, 400, 150, 300, 200);
        InsertUsage(connection, "tomorrow-start", LocalMidnightUtc(day.AddDays(1)), 9, 9, 0, 0, 0);

        var quotaAt = DateTimeOffset.UtcNow.AddHours(-1);
        InsertQuota(connection, "5-hour", "codex", 300, 16, quotaAt);
        InsertQuota(connection, "weekly", "codex", 10080, 61, quotaAt.AddMinutes(1));
        InsertQuota(connection, "reserve", "base_model_inference", 10080, 0, quotaAt.AddMinutes(2));
        InsertQuota(connection, "5-hour", "codex", 300, 10, quotaAt.AddHours(-25));
        InsertQuota(connection, "weekly", "codex", 10080, 70, quotaAt.AddDays(-10));
        InsertQuota(connection, "5-hour", "other", 300, 1, quotaAt.AddMinutes(3));
    }

    private static void InsertQuota(
        SqliteConnection connection,
        string kind,
        string limitId,
        int windowMinutes,
        double usedPercent,
        DateTimeOffset sampledAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quota_samples(kind, used_percent, reset_at, window_minutes, sampled_at, source, limit_id)
            VALUES ($kind, $used, $reset, $window, $sampled, 'TEST', $limit);
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$used", usedPercent);
        command.Parameters.AddWithValue("$reset", sampledAt.AddHours(5).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$window", windowMinutes);
        command.Parameters.AddWithValue("$sampled", sampledAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limitId);
        command.ExecuteNonQuery();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static long ScalarInt64FromFile(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return ScalarInt64(connection, sql);
    }

    private static DateTime LocalNoon(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Local)
            .ToUniversalTime();

    private static DateTime LocalMidnightUtc(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local)
            .ToUniversalTime();

    private static void InsertUsage(
        SqliteConnection connection,
        string identity,
        DateTime eventAt,
        long total,
        long input,
        long cached,
        long output,
        long reasoning)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_samples
                (identity, session_id, event_at, total_delta, input_delta, cached_delta,
                 output_delta, reasoning_delta, context_window, cumulative_total)
            VALUES
                ($identity, 'session', $event_at, $total, $input, $cached,
                 $output, $reasoning, NULL, $total);
            """;
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue(
            "$event_at",
            eventAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$input", input);
        command.Parameters.AddWithValue("$cached", cached);
        command.Parameters.AddWithValue("$output", output);
        command.Parameters.AddWithValue("$reasoning", reasoning);
        command.ExecuteNonQuery();
    }
}
