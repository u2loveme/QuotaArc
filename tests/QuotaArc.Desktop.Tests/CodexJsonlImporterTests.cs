using Microsoft.Data.Sqlite;
using QuotaArc.Desktop;
using System.Text.Json;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class CodexJsonlImporterTests
{
    [Fact]
    public void ImportsExistingPythonFixtureWithoutDoubleCountingAndContinuesFromOffset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "telemetry.sqlite3");
        var source = Path.Combine(directory, "session-a.jsonl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "token-events.jsonl"), source);
        CreateSchema(database);

        try
        {
            var importer = new CodexJsonlImporter();
            var first = importer.Import(database, [directory]);
            var second = importer.Import(database, [directory]);

            Assert.Equal(4, first.EventsImported);
            Assert.Equal(4, first.QuotaSamplesImported);
            Assert.Equal(new CodexImportResult(0, 0), second);
            using (var connection = Open(database))
            {
                Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM usage_samples;"));
                Assert.Equal(220L, Scalar(connection, "SELECT SUM(total_delta) FROM usage_samples;"));
                Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM quota_samples WHERE limit_id='codex';"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM import_state;"));
            }

            File.AppendAllText(source,
                "\n{\"timestamp\":\"2026-08-27T00:02:00Z\",\"ordinal\":5,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":20,\"input_tokens\":18,\"output_tokens\":2}}}}\n");
            var appended = importer.Import(database, [directory]);

            Assert.Equal(1, appended.EventsImported);
            using (var afterAppend = Open(database))
            {
                Assert.Equal(230L, Scalar(afterAppend, "SELECT SUM(total_delta) FROM usage_samples;"));
                Assert.Equal(10L, Scalar(afterAppend, "SELECT total_delta FROM usage_samples WHERE identity='session-a:5';"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DoesNotCreateAMissingProductionDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "missing.sqlite3");
        try
        {
            Assert.Throws<FileNotFoundException>(() => new CodexJsonlImporter().Import(database, [directory]));
            Assert.False(File.Exists(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppServerRefreshUsesNamedCodexAndReservePools()
    {
        const string response = """
            {"rateLimitsByLimitId":{
              "codex":{"primary":{"usedPercent":28,"windowDurationMins":300,"resetsAt":1790264660},
                       "secondary":{"usedPercent":72,"windowDurationMins":10080,"resetsAt":1790583674}},
              "base_model_inference":{"primary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1790869418}},
              "other":{"primary":{"usedPercent":99,"windowDurationMins":300}}}}
            """;
        using var document = JsonDocument.Parse(response);
        var samples = CodexAppServerClient.ParseQuotas(document.RootElement, DateTimeOffset.UtcNow);

        Assert.NotNull(samples);
        Assert.Collection(samples,
            sample => Assert.Equal(("5-hour", "codex", 28d, 300), (sample.Kind, sample.LimitId, sample.UsedPercent, sample.WindowMinutes)),
            sample => Assert.Equal(("weekly", "codex", 72d, 10080), (sample.Kind, sample.LimitId, sample.UsedPercent, sample.WindowMinutes)),
            sample => Assert.Equal(("reserve", "base_model_inference", 0d, 10080), (sample.Kind, sample.LimitId, sample.UsedPercent, sample.WindowMinutes)));
    }

    [Fact]
    public void QuotaAlertStateKeepsExistingThresholdCrossingRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "telemetry.sqlite3");
        CreateSchema(database);
        try
        {
            using (var connection = OpenReadWrite(database))
            {
                foreach (var (used, minute) in new[] { (60d, 0), (85d, 1) })
                {
                    using var transaction = connection.BeginTransaction();
                    var at = DateTimeOffset.Parse($"2026-09-24T12:{minute:00}:00+00:00");
                    QuotaAlertStore.StoreQuota(connection, transaction,
                        new LiveQuotaSample("5-hour", "codex", used, 300, null, at), "APP_SERVER_LIVE", notify: true);
                    transaction.Commit();
                }
            }

            using var readOnly = Open(database);
            using var command = readOnly.CreateCommand();
            command.CommandText = "SELECT threshold, event_type FROM alert_events ORDER BY id;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(25d, reader.GetDouble(0));
            Assert.Equal("LOW_THRESHOLD", reader.GetString(1));
            Assert.False(reader.Read());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PausingAlertsDoesNotSuppressQuotaResetAuditEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "telemetry.sqlite3");
        CreateSchema(database);
        try
        {
            var firstAt = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
            using (var connection = OpenReadWrite(database))
            {
                using (var setupCommand = connection.CreateCommand())
                {
                    setupCommand.CommandText = "INSERT INTO app_settings(key, value) VALUES ('alert_paused', '1');";
                    setupCommand.ExecuteNonQuery();
                }

                using (var transaction = connection.BeginTransaction())
                {
                    QuotaAlertStore.StoreQuota(connection, transaction,
                        new LiveQuotaSample("5-hour", "codex", 8, 300, firstAt.AddHours(5), firstAt),
                        "APP_SERVER_LIVE", notify: true);
                    transaction.Commit();
                }
                using (var transaction = connection.BeginTransaction())
                {
                    QuotaAlertStore.StoreQuota(connection, transaction,
                        new LiveQuotaSample("5-hour", "codex", 0, 300, firstAt.AddHours(10), firstAt.AddHours(5)),
                        "APP_SERVER_LIVE", notify: true);
                    transaction.Commit();
                }
            }

            using var readOnly = Open(database);
            using var command = readOnly.CreateCommand();
            command.CommandText = "SELECT event_type, event_at FROM alert_events ORDER BY id;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("RESET", reader.GetString(0));
            Assert.Equal(firstAt.AddHours(5).ToString("O"), DateTimeOffset.Parse(reader.GetString(1)).ToString("O"));
            Assert.False(reader.Read());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void QuotaAlertThresholdsCollapseAndPersistAcrossCyclesAndRestarts()
    {
        Assert.Equal((1, 25d), RunThresholds("5-hour", (30, null, 0), (24, null, 1)));
        Assert.Equal((1, 15d), RunThresholds("weekly", (30, null, 0), (14, null, 1)));
        Assert.Equal((1, 10d), RunThresholds("weekly", (31, null, 0), (9, null, 1)));
        Assert.Equal((1, 5d), RunThresholds("weekly", (30, null, 0), (4, null, 1)));
        Assert.Equal((1, 0d), RunThresholds("weekly", (30, null, 0), (0, null, 1)));
        Assert.Equal((1, 0d), RunThresholds("weekly", (4, null, 0), (0, null, 1)));

        // A terminal state and every intermediate threshold are consumed once.
        Assert.Equal((1, 0d), RunThresholds("weekly", (30, null, 0), (0, null, 1), (0, null, 2)));
        Assert.Equal((1, 10d), RunThresholds("weekly", (31, null, 0), (9, null, 1), (12, null, 2), (9, null, 3)));

        // A refill without a new reset identity does not re-arm the same cycle.
        Assert.Equal((1, 25d), RunThresholds("weekly", (100, "2026-09-24T00:00:00Z", 0),
            (24, "2026-09-24T00:00:00Z", 1), (100, "2026-09-24T00:00:00Z", 2),
            (24, "2026-09-24T00:00:00Z", 3)));

        // A new reset identity starts a fresh cycle after a persisted restart.
        Assert.Equal((2, 25d), RunThresholds("weekly", (100, "2026-09-24T00:00:00Z", 0),
            (24, "2026-09-24T00:00:00Z", 1), (100, "2026-09-25T00:00:00Z", 2),
            (24, "2026-09-25T00:00:00Z", 3)));

        // An older live sample cannot replay an alert after a newer sample.
        Assert.Equal((0, 0d), RunThresholds("weekly", (30, null, 10), (24, null, 5)));
    }

    [Fact]
    public void QuotaAlertStateConsumesEveryIntermediateThresholdForTheCycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-alert-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "telemetry.sqlite3");
        CreateSchema(database);
        try
        {
            using (var connection = OpenReadWrite(database))
            using (var transaction = connection.BeginTransaction())
            {
                QuotaAlertStore.StoreQuota(connection, transaction,
                    new LiveQuotaSample("weekly", "codex", 69, 10080, null,
                        DateTimeOffset.Parse("2026-09-24T12:00:00Z")),
                    "APP_SERVER_LIVE", notify: true);
                transaction.Commit();
            }

            using (var connection = OpenReadWrite(database))
            using (var transaction = connection.BeginTransaction())
            {
                QuotaAlertStore.StoreQuota(connection, transaction,
                    new LiveQuotaSample("weekly", "codex", 91, 10080, null,
                        DateTimeOffset.Parse("2026-09-24T12:01:00Z")),
                    "APP_SERVER_LIVE", notify: true);
                transaction.Commit();
            }

            using var readOnly = Open(database);
            using var command = readOnly.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key='quota_state.weekly';";
            using var document = JsonDocument.Parse((string)command.ExecuteScalar()!);
            var fired = document.RootElement.GetProperty("fired")
                .EnumerateArray().Select(item => item.GetDouble()).ToArray();
            Assert.Equal(new[] { 25d, 15d, 10d }, fired.OrderByDescending(value => value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("weekly: 0% remaining", "weekly-quota")]
    [InlineData("5-hour: 25% remaining", "5-hour-quota")]
    [InlineData("reserve: 5% remaining", "reserve-quota")]
    [InlineData("Windows notification delivery is enabled.", "quota-alert")]
    public void WindowsNotificationTagsRemainStablePerQuotaSource(string message, string expectedTag) =>
        Assert.Equal(expectedTag, WindowsAppNotificationService.GetNotificationTag(message));

    private static (int Count, double Threshold) RunThresholds(
        string kind,
        params (int Remaining, string? ResetAt, int Minutes)[] values)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-alert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "telemetry.sqlite3");
        CreateSchema(database);
        try
        {
            foreach (var (remaining, resetAt, minutes) in values)
            {
                using var connection = OpenReadWrite(database);
                using var transaction = connection.BeginTransaction();
                QuotaAlertStore.StoreQuota(connection, transaction,
                    new LiveQuotaSample(
                        kind, "codex", 100 - remaining, kind == "weekly" ? 10080 : 300,
                        resetAt is null ? null : DateTimeOffset.Parse(resetAt),
                        DateTimeOffset.Parse($"2026-09-24T12:{minutes:00}:00Z")),
                    "APP_SERVER_LIVE", notify: true);
                transaction.Commit();
            }

            using var readOnly = Open(database);
            using var command = readOnly.CreateCommand();
            command.CommandText = """
                SELECT threshold FROM alert_events
                WHERE event_type='LOW_THRESHOLD' ORDER BY id;
                """;
            using var reader = command.ExecuteReader();
            var thresholds = new List<double>();
            while (reader.Read()) thresholds.Add(reader.GetDouble(0));
            return (thresholds.Count, thresholds.Count == 0 ? 0 : thresholds[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void CreateSchema(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE import_state(path TEXT PRIMARY KEY, offset INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE usage_samples(
                identity TEXT PRIMARY KEY, session_id TEXT NOT NULL, event_at TEXT NOT NULL,
                total_delta INTEGER NOT NULL, input_delta INTEGER NOT NULL, cached_delta INTEGER NOT NULL,
                output_delta INTEGER NOT NULL, reasoning_delta INTEGER NOT NULL, context_window INTEGER,
                cumulative_total INTEGER NOT NULL);
            CREATE TABLE quota_samples(
                id INTEGER PRIMARY KEY, kind TEXT NOT NULL, used_percent REAL NOT NULL, reset_at TEXT,
                window_minutes INTEGER, sampled_at TEXT NOT NULL, source TEXT NOT NULL, limit_id TEXT);
            CREATE TABLE alert_events(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, threshold REAL,
                event_type TEXT NOT NULL, event_at TEXT NOT NULL, detail TEXT NOT NULL);
            CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
