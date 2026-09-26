using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Windows.Media;

namespace QuotaArc.Desktop;

public sealed record TodayUsageSnapshot(
    DateOnly LocalDay,
    long SampleCount,
    long Total,
    long Input,
    long Cached,
    long Output,
    long Reasoning,
    string SqliteVersion);

public sealed record DailyUsageSnapshot(
    DateOnly LocalDay,
    long SampleCount,
    long Total,
    long Input,
    long Cached,
    long Output,
    long Reasoning);

public sealed record QuotaUsageSnapshot(
    string Kind,
    string LimitId,
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset SampledAt,
    DateTimeOffset? ResetAt,
    string Source);

public sealed record QuotaHistoryPoint(
    string Kind,
    DateTimeOffset SampledAt,
    double RemainingPercent,
    DateTimeOffset? ResetAt = null,
    string? Source = null);

public sealed record AlertEventSnapshot(
    long Id,
    string Kind,
    string EventType,
    DateTimeOffset EventAt,
    string Message,
    Color AccentColor,
    Brush Accent,
    bool IsUnread);

public sealed record QuotaResetEventSnapshot(DateTimeOffset EventAt, string Kind);

public sealed record DashboardUsageSnapshot(
    TodayUsageSnapshot Today,
    IReadOnlyList<DailyUsageSnapshot> History,
    QuotaUsageSnapshot? FiveHour,
    QuotaUsageSnapshot? Weekly,
    QuotaUsageSnapshot? Reserve,
    IReadOnlyList<QuotaHistoryPoint> QuotaHistory,
    IReadOnlyList<QuotaResetEventSnapshot> QuotaResetEvents,
    DateTimeOffset ReadAt,
    int DailyHistoryDays,
    int QuotaHistoryHours);

public sealed class SqliteTodayUsageReader
{
    private const string LastReadAlertSetting = "desktop_last_read_alert_id";
    private const string DesktopNotificationsSetting = "desktop_notifications_enabled";
    private const string DesktopNotificationSoundSetting = "desktop_notification_sound_enabled";
    private const string NotificationDeliverySetting = "notification_delivery_mode";
    private const string NotificationBadgeSetting = "notification_badge_mode";
    private const string NotificationSoundModeSetting = "notification_sound_mode";

    public static string ProductionDatabasePath => Path.Combine(
        QuotaArcProfilePaths.LocalAppDataRoot,
        "QuotaArc",
        "telemetry.sqlite3");

    public DashboardUsageSnapshot ReadDashboard(
        DateOnly localDay,
        int? historyDays = null,
        int? quotaHours = null,
        string? databasePath = null,
        bool includeHistory = true)
    {
        if (historyDays is not null && !new[] { 7, 14, 30, 90 }.Contains(historyDays.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(historyDays));
        }
        if (quotaHours is not null && !new[] { 6, 12, 24, 168, 336, 720 }.Contains(quotaHours.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(quotaHours));
        }

        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var savedRanges = ReadRangePreferences(connection, transaction);
        var dailyRange = historyDays ?? savedRanges.Daily;
        var quotaRange = quotaHours ?? savedRanges.Quota;
        var readAt = DateTimeOffset.UtcNow;
        var today = ReadLocalDay(connection, transaction, localDay);
        var history = includeHistory
            ? ReadHistory(connection, transaction, localDay, Math.Max(dailyRange, 7))
            : Array.Empty<DailyUsageSnapshot>();
        var fiveHour = ReadLatestQuota(connection, transaction, "5-hour", "codex", 300);
        var weekly = ReadLatestQuota(connection, transaction, "weekly", "codex", 10080);
        var reserve = ReadLatestQuota(connection, transaction, "reserve", "base_model_inference", 10080);
        var quotaHistory = includeHistory
            ? ReadQuotaHistory(connection, transaction, readAt, Math.Max(quotaRange, 168))
            : Array.Empty<QuotaHistoryPoint>();
        var quotaResetEvents = includeHistory
            ? ReadQuotaResetEvents(connection, transaction, readAt, Math.Max(quotaRange, 168))
            : Array.Empty<QuotaResetEventSnapshot>();
        transaction.Commit();

        return new DashboardUsageSnapshot(
            today,
            history,
            fiveHour,
            weekly,
            reserve,
            quotaHistory,
            quotaResetEvents,
            readAt,
            dailyRange,
            quotaRange);
    }

    public void SaveHistoryRangePreference(string key, int value, string? databasePath = null)
    {
        var isValid = key switch
        {
            "history_daily_range" => new[] { 7, 14, 30, 90 }.Contains(value),
            "history_quota_range" => new[] { 6, 12, 24, 168, 336, 720 }.Contains(value),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        if (!isValid)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadWrite(path);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<AlertEventSnapshot> ReadRecentAlertEvents(
        string? databasePath = null,
        int limit = 30)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var lastReadId = ReadLastReadAlertId(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, kind, event_type, event_at, detail
            FROM alert_events
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var events = new List<AlertEventSnapshot>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!TryParseTimestamp(reader.GetString(3), out var eventAt))
                {
                    continue;
                }

                var eventType = reader.GetString(2);
                events.Add(new AlertEventSnapshot(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    eventType,
                    eventAt,
                    GetAlertMessage(reader.GetString(4)),
                    AlertAccent(eventType),
                    Brushes.Transparent,
                    reader.GetInt64(0) > lastReadId));
            }
        }

        transaction.Commit();
        return events;
    }

    public IReadOnlyList<AlertEventSnapshot> ReadAlertEventsAfter(
        long lastSeenId,
        string? databasePath = null,
        int limit = 100)
    {
        if (lastSeenId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSeenId));
        }
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var lastReadId = ReadLastReadAlertId(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, kind, event_type, event_at, detail
            FROM alert_events
            WHERE id > $last_seen_id
            ORDER BY id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$last_seen_id", lastSeenId);
        command.Parameters.AddWithValue("$limit", limit);
        var events = new List<AlertEventSnapshot>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!TryParseTimestamp(reader.GetString(3), out var eventAt))
                {
                    continue;
                }

                var eventType = reader.GetString(2);
                events.Add(new AlertEventSnapshot(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    eventType,
                    eventAt,
                    GetAlertMessage(reader.GetString(4)),
                    AlertAccent(eventType),
                    Brushes.Transparent,
                    reader.GetInt64(0) > lastReadId));
            }
        }

        transaction.Commit();
        return events;
    }

    public void MarkRecentAlertEventsRead(string? databasePath = null)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadWrite(path);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var latestCommand = connection.CreateCommand();
        latestCommand.Transaction = transaction;
        latestCommand.CommandText = "SELECT COALESCE(MAX(id), 0) FROM alert_events;";
        var latestId = Convert.ToInt64(latestCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var saveCommand = connection.CreateCommand();
        saveCommand.Transaction = transaction;
        saveCommand.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        saveCommand.Parameters.AddWithValue("$key", LastReadAlertSetting);
        saveCommand.Parameters.AddWithValue("$value", latestId.ToString(CultureInfo.InvariantCulture));
        saveCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public string ReadNotificationDeliveryMode(string? databasePath = null)
    {
        var saved = ReadAppSetting(NotificationDeliverySetting, databasePath);
        if (saved is "off" or "quiet" or "banner")
        {
            return saved;
        }

        var legacyEnabled = ReadDesktopNotificationsEnabled(databasePath);
        return legacyEnabled ? "quiet" : "off";
    }

    public void SaveNotificationDeliveryMode(string mode, string? databasePath = null) =>
        SaveAppSetting(NotificationDeliverySetting, ValidateMode(mode, "off", "quiet", "banner"), databasePath);

    public string ReadNotificationBadgeMode(string? databasePath = null)
    {
        var saved = ReadAppSetting(NotificationBadgeSetting, databasePath);
        return saved is "off" or "dot" or "count" ? saved : "dot";
    }

    public void SaveNotificationBadgeMode(string mode, string? databasePath = null) =>
        SaveAppSetting(NotificationBadgeSetting, ValidateMode(mode, "off", "dot", "count"), databasePath);

    public string ReadNotificationSoundMode(string? databasePath = null)
    {
        var saved = ReadAppSetting(NotificationSoundModeSetting, databasePath);
        if (saved is "off" or "important" or "all")
        {
            return saved;
        }

        return ReadDesktopNotificationSoundEnabled(databasePath) ? "all" : "off";
    }

    public void SaveNotificationSoundMode(string mode, string? databasePath = null) =>
        SaveAppSetting(NotificationSoundModeSetting, ValidateMode(mode, "off", "important", "all"), databasePath);

    private static string? ReadAppSetting(string key, string? databasePath)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            return null;
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SaveAppSetting(string key, string value, string? databasePath)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadWrite(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ValidateMode(string mode, params string[] allowedModes) =>
        allowedModes.Contains(mode, StringComparer.Ordinal)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported notification preference.");

    public bool ReadDesktopNotificationsEnabled(string? databasePath = null)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            return true;
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", DesktopNotificationsSetting);
        return !string.Equals(command.ExecuteScalar() as string, "0", StringComparison.Ordinal);
    }

    public void SaveDesktopNotificationsEnabled(bool enabled, string? databasePath = null)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadWrite(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", DesktopNotificationsSetting);
        command.Parameters.AddWithValue("$value", enabled ? "1" : "0");
        command.ExecuteNonQuery();
    }

    public bool ReadDesktopNotificationSoundEnabled(string? databasePath = null)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            return false;
        }

        using var connection = OpenReadOnly(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", DesktopNotificationSoundSetting);
        return string.Equals(command.ExecuteScalar() as string, "1", StringComparison.Ordinal);
    }

    public void SaveDesktopNotificationSoundEnabled(bool enabled, string? databasePath = null)
    {
        var path = databasePath ?? ProductionDatabasePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local usage database was not found.", path);
        }

        using var connection = OpenReadWrite(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", DesktopNotificationSoundSetting);
        command.Parameters.AddWithValue("$value", enabled ? "1" : "0");
        command.ExecuteNonQuery();
    }

    private static long ReadLastReadAlertId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT value FROM app_settings WHERE key = $key LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", LastReadAlertSetting);
        return long.TryParse(command.ExecuteScalar() as string, NumberStyles.None,
            CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static string GetAlertMessage(string detail)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? detail;
            }
        }
        catch (JsonException)
        {
        }

        return detail;
    }

    private static Color AlertAccent(string eventType) => eventType switch
    {
        "LOW_THRESHOLD" => Color.FromRgb(255, 96, 117),
        "RESET" or "SOURCE_RESTORED" or "RECOVERY" => Color.FromRgb(67, 214, 164),
        "SOURCE_STALE" => Color.FromRgb(255, 162, 74),
        _ => Color.FromRgb(139, 124, 255)
    };

    private static (int Daily, int Quota) ReadRangePreferences(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT key, value
            FROM app_settings
            WHERE key IN ('history_daily_range', 'history_quota_range');
            """;
        var daily = 14;
        var quota = 24;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(1);
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            if (reader.GetString(0) == "history_daily_range" && new[] { 7, 14, 30, 90 }.Contains(parsed))
            {
                daily = parsed;
            }
            else if (reader.GetString(0) == "history_quota_range" && new[] { 6, 12, 24, 168, 336, 720 }.Contains(parsed))
            {
                quota = parsed;
            }
        }

        return (daily, quota);
    }

    private static TodayUsageSnapshot ReadLocalDay(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly localDay)
    {
        var localStart = localDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var localEnd = localDay.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var startUtc = new DateTimeOffset(localStart).ToUniversalTime();
        var endUtc = new DateTimeOffset(localEnd).ToUniversalTime();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) AS sample_count,
                   COALESCE(SUM(total_delta), 0) AS total,
                   COALESCE(SUM(input_delta), 0) AS input,
                   COALESCE(SUM(cached_delta), 0) AS cached,
                   COALESCE(SUM(output_delta), 0) AS output,
                   COALESCE(SUM(reasoning_delta), 0) AS reasoning
            FROM usage_samples
            WHERE event_at >= $start_utc AND event_at < $end_utc;
            """;
        command.Parameters.AddWithValue("$start_utc", startUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end_utc", endUtc.ToString("O", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The usage summary query returned no result row.");
        }

        return new TodayUsageSnapshot(
            localDay,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            connection.ServerVersion);
    }

    private static IReadOnlyList<DailyUsageSnapshot> ReadHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly localDay,
        int days)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT date(event_at, 'localtime') AS local_day,
                   COUNT(*) AS sample_count,
                   SUM(total_delta) AS total,
                   SUM(input_delta) AS input,
                   SUM(cached_delta) AS cached,
                   SUM(output_delta) AS output,
                   SUM(reasoning_delta) AS reasoning
            FROM usage_samples
            WHERE event_at >= datetime('now', $lookback)
            GROUP BY date(event_at, 'localtime')
            ORDER BY local_day;
            """;
        command.Parameters.AddWithValue("$lookback", $"-{days} days");

        var rows = new Dictionary<DateOnly, DailyUsageSnapshot>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var day = DateOnly.ParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
                rows[day] = new DailyUsageSnapshot(
                    day,
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6));
            }
        }

        return Enumerable.Range(0, days)
            .Select(offset => localDay.AddDays(offset - days + 1))
            .Select(day => rows.TryGetValue(day, out var snapshot)
                ? snapshot
                : new DailyUsageSnapshot(day, 0, 0, 0, 0, 0, 0))
            .ToArray();
    }

    private static QuotaUsageSnapshot? ReadLatestQuota(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string limitId,
        int windowMinutes)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT used_percent, reset_at, sampled_at, source
            FROM quota_samples
            WHERE kind = $kind AND limit_id = $limit_id AND window_minutes = $window
            ORDER BY sampled_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$limit_id", limitId);
        command.Parameters.AddWithValue("$window", windowMinutes);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (!TryParseTimestamp(reader.GetString(2), out var sampledAt))
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        if (!reader.IsDBNull(1) && TryParseTimestamp(reader.GetString(1), out var parsedReset))
        {
            resetAt = parsedReset;
        }

        return new QuotaUsageSnapshot(
            kind,
            limitId,
            reader.GetDouble(0),
            windowMinutes,
            sampledAt,
            resetAt,
            reader.GetString(3));
    }

    private static IReadOnlyList<QuotaHistoryPoint> ReadQuotaHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset readAt,
        int hours)
    {
        var start = readAt.AddHours(-hours);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, sampled_at, 100.0 - used_percent AS remaining, reset_at, source
            FROM quota_samples
            WHERE limit_id = 'codex'
              AND kind IN ('5-hour', 'weekly')
              AND sampled_at >= $start
            ORDER BY sampled_at;
            """;
        command.Parameters.AddWithValue(
            "$start",
            start.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));

        var points = new List<QuotaHistoryPoint>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryParseTimestamp(reader.GetString(1), out var sampledAt)
                || sampledAt > readAt
                || sampledAt < start)
            {
                continue;
            }

            DateTimeOffset? resetAt = null;
            if (!reader.IsDBNull(3)
                && TryParseTimestamp(reader.GetString(3), out var parsedResetAt))
            {
                resetAt = parsedResetAt;
            }

            points.Add(new QuotaHistoryPoint(
                reader.GetString(0),
                sampledAt,
                reader.GetDouble(2),
                resetAt,
                reader.GetString(4)));
        }

        return points.OrderBy(point => point.SampledAt).ToArray();
    }

    private static IReadOnlyList<QuotaResetEventSnapshot> ReadQuotaResetEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset readAt,
        int hours)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, event_at
            FROM alert_events
            WHERE event_type = 'RESET'
              AND kind IN ('5-hour', 'weekly')
              AND event_at >= $start
              AND event_at <= $read_at
            ORDER BY event_at;
            """;
        command.Parameters.AddWithValue(
            "$start",
            readAt.AddHours(-hours).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$read_at",
            readAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));

        var events = new List<QuotaResetEventSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryParseTimestamp(reader.GetString(1), out var eventAt))
            {
                events.Add(new QuotaResetEventSnapshot(eventAt, reader.GetString(0)));
            }
        }

        return events;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static SqliteConnection OpenReadOnly(string path) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

    private static SqliteConnection OpenReadWrite(string path) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
}
