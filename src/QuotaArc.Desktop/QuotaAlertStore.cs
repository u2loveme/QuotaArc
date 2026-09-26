using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal static class QuotaAlertStore
{
    private static readonly double[] DefaultThresholds = [50, 25, 15, 10, 5];

    internal static bool StoreQuota(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LiveQuotaSample sample,
        string source,
        bool notify)
    {
        var current = ReadLatestQuota(connection, transaction, sample.Kind, sample.LimitId);
        if (source != "APP_SERVER_LIVE" && current is { Source: "APP_SERVER_LIVE" }
            && current.SampledAt >= sample.SampledAt)
        {
            return false;
        }

        var inserted = CodexJsonlImporter.InsertQuota(
            connection, transaction, sample.Kind, sample.UsedPercent, sample.ResetAt,
            sample.WindowMinutes, sample.SampledAt, sample.LimitId, source);

        if (notify && sample.LimitId == "codex"
            && (current is null || sample.SampledAt >= current.SampledAt))
        {
            Observe(connection, transaction, sample);
        }
        return inserted;
    }

    private static void Observe(SqliteConnection connection, SqliteTransaction transaction, LiveQuotaSample sample)
    {
        var alertsPaused = string.Equals(
            ReadSetting(connection, transaction, "alert_paused"), "1", StringComparison.Ordinal);
        var thresholds = alertsPaused ? [] : ReadThresholds(connection, transaction, sample.Kind);
        var stateKey = "quota_state." + sample.Kind;
        var state = ReadState(connection, transaction, stateKey);
        var remaining = Math.Clamp(100 - sample.UsedPercent, 0, 100);
        var old = state.Value.TryGetProperty("remaining", out var oldValue) && oldValue.ValueKind == JsonValueKind.Number
            ? oldValue.GetDouble()
            : (double?)null;
        var epoch = sample.ResetAt is not null
            ? CodexJsonlImporter.ToPythonIso(sample.ResetAt.Value)
            : ReadString(state, "epoch");
        var resetEpoch = ReadString(state, "reset_at");
        var lastSample = ReadTimestamp(state, "last_sample");
        var initialized = ReadBool(state, "initialized");
        var fired = ReadFired(state);

        if (!initialized)
        {
            SaveState(connection, transaction, stateKey,
                new AlertState(true, remaining, epoch, epoch, fired, sample.SampledAt, ReadBool(state, "stale")));
            return;
        }

        DateTimeOffset? expectedReset = TryParseTimestamp(resetEpoch, out var parsedExpectedReset)
            ? parsedExpectedReset
            : null;
        var crossedExpectedReset = expectedReset is { } boundary
            && lastSample is { } previousSample
            && boundary > previousSample
            && boundary <= sample.SampledAt;
        var resetEpochChanged = epoch is not null
            && resetEpoch is not null
            && epoch != resetEpoch;
        var confirmedEpochRefill = resetEpochChanged
            && old is not null
            && remaining - old.Value >= 10
            && remaining >= 75;
        var reset = crossedExpectedReset || confirmedEpochRefill || (old is not null
            && remaining - old.Value >= 50
            && remaining >= 90);

        if (reset)
        {
            // Alert pause silences threshold/recovery notifications, but does
            // not erase quota history. Resets remain audit events either way.
            Record(connection, transaction, sample.Kind, "RESET", old, remaining, null,
                $"{sample.Kind} quota refreshed",
                crossedExpectedReset ? expectedReset ?? sample.SampledAt : sample.SampledAt);
            fired.Clear();
        }
        else if (!alertsPaused && old is not null && remaining - old.Value >= 10)
        {
            Record(connection, transaction, sample.Kind, "RECOVERY", old, remaining, null,
                $"{sample.Kind} quota recovering", sample.SampledAt);
        }

        if (!alertsPaused)
        {
            foreach (var threshold in thresholds)
            {
                if (old is not null && old.Value >= threshold && threshold > remaining
                    && !fired.Contains(threshold))
                {
                    fired.Add(threshold);
                    Record(connection, transaction, sample.Kind, "LOW_THRESHOLD", old, remaining,
                        threshold, $"{sample.Kind}: {remaining:0}% remaining (crossed {threshold:0}%)",
                        sample.SampledAt);
                }
            }
        }

        SaveState(connection, transaction, stateKey,
            new AlertState(true, remaining, epoch, ReadString(state, "epoch") ?? epoch, fired, sample.SampledAt, ReadBool(state, "stale")));
    }

    private static double[] ReadThresholds(SqliteConnection connection, SqliteTransaction transaction, string kind)
    {
        var raw = ReadSetting(connection, transaction, "thresholds." + kind)
            ?? ReadSetting(connection, transaction, "thresholds");
        if (raw is null)
        {
            WriteSetting(connection, transaction, "thresholds", "50,25,15,10,5");
            return DefaultThresholds;
        }

        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
                ? threshold
                : double.NaN)
            .Where(value => value > 0 && value < 100)
            .Distinct()
            .OrderDescending()
            .ToArray();
        return values.Length == 0 ? DefaultThresholds : values;
    }

    private static AlertStateJson ReadState(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        var raw = ReadSetting(connection, transaction, key);
        if (raw is null) return AlertStateJson.Empty;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return new AlertStateJson(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return AlertStateJson.Empty;
        }
    }

    private static List<double> ReadFired(AlertStateJson state) =>
        state.Value.TryGetProperty("fired", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetDouble()).ToList()
            : [];

    private static bool ReadBool(AlertStateJson state, string name) =>
        state.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(AlertStateJson state, string name) =>
        state.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(AlertStateJson state, string name) =>
        TryParseTimestamp(ReadString(state, name), out var timestamp) ? timestamp : null;

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    private static void SaveState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        AlertState state)
    {
        var json = JsonSerializer.Serialize(new
        {
            initialized = state.Initialized,
            remaining = state.Remaining,
            reset_at = state.ResetAt,
            epoch = state.Epoch,
            fired = state.Fired,
            last_sample = CodexJsonlImporter.ToPythonIso(state.LastSample),
            stale = state.Stale
        });
        WriteSetting(connection, transaction, key, json);
    }

    private static void Record(SqliteConnection connection, SqliteTransaction transaction, string kind,
        string eventType, double? oldPercent, double newPercent, double? threshold, string message,
        DateTimeOffset eventAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
            VALUES ($kind, $threshold, $event_type, $event_at, $detail);
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$threshold", (object?)threshold ?? DBNull.Value);
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$event_at", CodexJsonlImporter.ToPythonIso(eventAt));
        command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new
        {
            old_percent = oldPercent,
            new_percent = newPercent,
            message
        }));
        command.ExecuteNonQuery();
    }

    private static LatestQuota? ReadLatestQuota(SqliteConnection connection, SqliteTransaction transaction,
        string kind, string limitId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sampled_at, source FROM quota_samples
            WHERE kind=$kind AND limit_id=$limit
            ORDER BY sampled_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$limit", limitId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new LatestQuota(ParseTimestamp(reader.GetString(0)), reader.GetString(1));
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string? ReadSetting(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM app_settings WHERE key=$key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteSetting(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private sealed record LatestQuota(DateTimeOffset SampledAt, string Source);
    private sealed record AlertState(bool Initialized, double Remaining, string? ResetAt, string? Epoch,
        List<double> Fired, DateTimeOffset LastSample, bool Stale);
    private sealed record AlertStateJson(JsonElement Value)
    {
        public static AlertStateJson Empty { get; } = new(JsonDocument.Parse("{}").RootElement.Clone());
    }
}
