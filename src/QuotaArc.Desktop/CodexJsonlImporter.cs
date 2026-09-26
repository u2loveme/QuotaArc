using System.IO;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal sealed record CodexImportResult(int EventsImported, int QuotaSamplesImported);

internal sealed class CodexJsonlImporter
{
    private const int BufferSize = 64 * 1024;

    public CodexImportResult Import(string databasePath, IEnumerable<string> roots)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("The existing usage database was not found; import will not create one.", databasePath);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        var imported = 0;
        var quotaCount = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                try
                {
                    var result = ImportFile(connection, file);
                    imported += result.EventsImported;
                    quotaCount += result.QuotaSamplesImported;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return new CodexImportResult(imported, quotaCount);
    }

    internal static CodexImportResult ImportFile(SqliteConnection connection, string path)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var offset = ReadOffset(connection, path);
        var fileLength = new FileInfo(path).Length;
        if (fileLength < offset) offset = 0;

        using var transaction = connection.BeginTransaction();
        var imported = 0;
        var quotas = 0;
        var buffer = new byte[BufferSize];
        using var line = new MemoryStream();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var lineStartOffset = offset;
        var fileReadOffset = offset;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            var chunkStartOffset = fileReadOffset;
            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                line.Write(buffer, segmentStart, index - segmentStart);
                var result = TryImportLine(connection, transaction, line.GetBuffer().AsSpan(0, checked((int)line.Length)), sessionId, lineStartOffset);
                imported += result.EventsImported;
                quotas += result.QuotaSamplesImported;
                line.SetLength(0);
                lineStartOffset = chunkStartOffset + index + 1;
                segmentStart = index + 1;
            }

            if (segmentStart < read) line.Write(buffer, segmentStart, read - segmentStart);
            fileReadOffset += read;
        }

        if (line.Length > 0)
        {
            var result = TryImportLine(connection, transaction, line.GetBuffer().AsSpan(0, checked((int)line.Length)), sessionId, lineStartOffset);
            imported += result.EventsImported;
            quotas += result.QuotaSamplesImported;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                INSERT INTO import_state(path, offset, updated_at) VALUES ($path, $offset, $updated)
                ON CONFLICT(path) DO UPDATE SET offset=excluded.offset, updated_at=excluded.updated_at;
                """;
            update.Parameters.AddWithValue("$path", path);
            update.Parameters.AddWithValue("$offset", fileReadOffset);
            update.Parameters.AddWithValue("$updated", ToPythonIso(DateTimeOffset.UtcNow));
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return new CodexImportResult(imported, quotas);
    }

    private static CodexImportResult TryImportLine(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReadOnlySpan<byte> bytes,
        string sessionId,
        long lineOffset)
    {
        var span = bytes;
        if (!span.IsEmpty && span[^1] == (byte)'\r') span = span[..^1];
        if (span.IsEmpty) return new CodexImportResult(0, 0);
        try
        {
            using var document = JsonDocument.Parse(span.ToArray());
            var root = document.RootElement;
            if (!TryProperty(root, "payload", out var payload)
                || !TryString(payload, "type", out var payloadType)
                || payloadType != "token_count"
                || !TryProperty(payload, "info", out var info)
                || !TryTimestamp(root, out var eventAt))
            {
                return new CodexImportResult(0, 0);
            }

            var ordinal = TryInt64(root, "ordinal", out var ordinalValue);
            var identity = ordinal
                ? $"{sessionId}:{ordinalValue.ToString(CultureInfo.InvariantCulture)}"
                : $"{sessionId}:{ToPythonIso(eventAt)}:{lineOffset.ToString(CultureInfo.InvariantCulture)}";
            if (IsSeen(connection, transaction, identity)) return new CodexImportResult(0, 0);

            var cumulative = ReadUsage(info, "total_token_usage");
            var delta = ReadDelta(connection, transaction, sessionId, eventAt, cumulative);
            InsertUsage(connection, transaction, identity, sessionId, eventAt, delta, cumulative,
                TryInt64(info, "model_context_window", out var context) ? context : null);
            var quotaCount = ImportQuotaPayload(connection, transaction,
                TryProperty(payload, "rate_limits", out var limits) ? limits : default, eventAt);
            return new CodexImportResult(1, quotaCount);
        }
        catch (JsonException)
        {
            return new CodexImportResult(0, 0);
        }
        catch (FormatException)
        {
            return new CodexImportResult(0, 0);
        }
        catch (OverflowException)
        {
            return new CodexImportResult(0, 0);
        }
    }

    private static long ReadOffset(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset FROM import_state WHERE path=$path;";
        command.Parameters.AddWithValue("$path", path);
        var value = command.ExecuteScalar();
        return value is long result ? result : 0;
    }

    private static bool IsSeen(SqliteConnection connection, SqliteTransaction transaction, string identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM usage_samples WHERE identity=$identity LIMIT 1;";
        command.Parameters.AddWithValue("$identity", identity);
        return command.ExecuteScalar() is not null;
    }

    private static Usage ReadUsage(JsonElement info, string name)
    {
        if (!TryProperty(info, name, out var usage)) return default;
        return new Usage(
            ReadNumber(usage, "total_tokens"),
            ReadNumber(usage, "input_tokens"),
            ReadNumber(usage, "cached_input_tokens"),
            ReadNumber(usage, "output_tokens"),
            ReadNumber(usage, "reasoning_output_tokens"));
    }

    private static long ReadNumber(JsonElement element, string name) =>
        TryInt64(element, name, out var value) ? value : 0;

    private static Usage ReadDelta(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset eventAt,
        Usage current)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cumulative_total FROM usage_samples
            WHERE session_id=$session AND event_at < $event_at
            ORDER BY event_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$event_at", ToPythonIso(eventAt));
        var priorValue = command.ExecuteScalar();
        if (priorValue is not long prior || current.Total < prior) return current;
        return new Usage(
            current.Total - prior,
            Math.Max(0, current.Input),
            Math.Max(0, current.Cached),
            Math.Max(0, current.Output),
            Math.Max(0, current.Reasoning));
    }

    private static void InsertUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identity,
        string sessionId,
        DateTimeOffset eventAt,
        Usage delta,
        Usage cumulative,
        long? contextWindow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO usage_samples
                (identity, session_id, event_at, total_delta, input_delta, cached_delta,
                 output_delta, reasoning_delta, context_window, cumulative_total)
            VALUES ($identity, $session, $event_at, $total, $input, $cached, $output, $reasoning, $context, $cumulative);
            """;
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$event_at", ToPythonIso(eventAt));
        command.Parameters.AddWithValue("$total", delta.Total);
        command.Parameters.AddWithValue("$input", delta.Input);
        command.Parameters.AddWithValue("$cached", delta.Cached);
        command.Parameters.AddWithValue("$output", delta.Output);
        command.Parameters.AddWithValue("$reasoning", delta.Reasoning);
        command.Parameters.AddWithValue("$context", (object?)contextWindow ?? DBNull.Value);
        command.Parameters.AddWithValue("$cumulative", cumulative.Total);
        command.ExecuteNonQuery();
    }

    private static int ImportQuotaPayload(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonElement payload,
        DateTimeOffset sampledAt)
    {
        if (payload.ValueKind != JsonValueKind.Object || !TryString(payload, "limit_id", out var limitId)) return 0;
        var isCodex = limitId == "codex";
        var isReserve = limitId == "base_model_inference";
        if (!isCodex && !isReserve) return 0;

        var added = 0;
        var hasReservePrimary = isReserve && TryProperty(payload, "primary", out var reservePrimary)
            && TryDouble(reservePrimary, "used_percent", out _);
        foreach (var (field, fallbackKind) in new[] { ("primary", isReserve ? "reserve" : "5-hour"), ("secondary", "weekly") })
        {
            if (isReserve && field == "secondary" && hasReservePrimary) continue;
            if (!TryProperty(payload, field, out var item)
                || !TryDouble(item, "used_percent", out var used)) continue;
            var kind = isReserve ? "reserve" : fallbackKind;
            var window = TryDouble(item, "window_minutes", out var windowValue) ? ToWindow(windowValue) : null;
            if (!isReserve && window is not null)
            {
                if (window == 300) kind = "5-hour";
                else if (window == 10080) kind = "weekly";
                else continue;
            }
            if (isReserve && window is not null && window != 10080) continue;
            DateTimeOffset? resetAt = TryDouble(item, "resets_at", out var resetSeconds)
                ? FromUnixSeconds(resetSeconds)
                : null;
            var quotaLimit = isReserve ? "base_model_inference" : "codex";
            var sample = new LiveQuotaSample(kind, quotaLimit, used, window, resetAt, sampledAt);
            if (QuotaAlertStore.StoreQuota(connection, transaction, sample, "LOCAL_CONFIRMED_CODEX_JSONL", notify: !isReserve)) added++;
        }
        return added;
    }

    internal static bool InsertQuota(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        double usedPercent,
        DateTimeOffset? resetAt,
        int? windowMinutes,
        DateTimeOffset sampledAt,
        string limitId,
        string source)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quota_samples(kind, used_percent, reset_at, window_minutes, sampled_at, source, limit_id)
            SELECT $kind, $used, $reset, $window, $sampled, $source, $limit
            WHERE NOT EXISTS (
                SELECT 1 FROM quota_samples WHERE kind=$kind AND sampled_at=$sampled AND limit_id=$limit
            );
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$used", usedPercent);
        command.Parameters.AddWithValue("$reset", resetAt is null ? DBNull.Value : ToPythonIso(resetAt.Value));
        command.Parameters.AddWithValue("$window", (object?)windowMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sampled", ToPythonIso(sampledAt));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$limit", limitId);
        return command.ExecuteNonQuery() > 0;
    }

    private static bool TryTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (!TryProperty(element, "timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String) return false;
        return DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    internal static string ToPythonIso(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var micros = utc.Ticks / 10 % 1_000_000;
        var fraction = micros == 0 ? string.Empty : "." + micros.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return $"{utc:yyyy-MM-dd'T'HH:mm:ss}{fraction}+00:00";
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return TryProperty(element, name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return TryProperty(element, name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    private static int? ToWindow(double value) =>
        double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue ? (int)value : null;

    private static DateTimeOffset? FromUnixSeconds(double value)
    {
        if (!double.IsFinite(value)) return null;
        try { return DateTimeOffset.UnixEpoch.AddSeconds(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private readonly record struct Usage(long Total, long Input, long Cached, long Output, long Reasoning);
}
