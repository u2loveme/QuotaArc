using System.IO;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal static class QuotaArcDatabaseInitializer
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["import_state"] = ["path", "offset", "updated_at"],
            ["usage_samples"] =
            [
                "identity", "session_id", "event_at", "total_delta", "input_delta", "cached_delta",
                "output_delta", "reasoning_delta", "context_window", "cumulative_total"
            ],
            ["quota_samples"] =
            ["id", "kind", "used_percent", "reset_at", "window_minutes", "sampled_at", "source", "limit_id"],
            ["alert_events"] = ["id", "kind", "threshold", "event_type", "event_at", "detail"],
            ["app_settings"] = ["key", "value"]
        };

    internal static void EnsureInitialized(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var targetPath = Path.GetFullPath(databasePath);
        if (File.Exists(targetPath))
        {
            ValidateExisting(targetPath);
            return;
        }

        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("The database path must have a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(targetPath)}.initialize-{Guid.NewGuid():N}.tmp");
        try
        {
            CreateCanonicalDatabase(temporaryPath);
            if (File.Exists(targetPath))
            {
                ValidateExisting(targetPath);
                return;
            }

            try
            {
                File.Move(temporaryPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                ValidateExisting(targetPath);
            }
        }
        finally
        {
            DeleteOwnedDatabaseFiles(temporaryPath);
        }
    }

    private static void CreateCanonicalDatabase(string path)
    {
        using (var connection = Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE import_state (
                    path TEXT PRIMARY KEY, offset INTEGER NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE usage_samples (
                    identity TEXT PRIMARY KEY, session_id TEXT NOT NULL, event_at TEXT NOT NULL,
                    total_delta INTEGER NOT NULL, input_delta INTEGER NOT NULL, cached_delta INTEGER NOT NULL,
                    output_delta INTEGER NOT NULL, reasoning_delta INTEGER NOT NULL, context_window INTEGER,
                    cumulative_total INTEGER NOT NULL);
                CREATE INDEX usage_by_day ON usage_samples(event_at);
                CREATE TABLE quota_samples (
                    id INTEGER PRIMARY KEY, kind TEXT NOT NULL, used_percent REAL NOT NULL, reset_at TEXT,
                    window_minutes INTEGER, sampled_at TEXT NOT NULL, source TEXT NOT NULL, limit_id TEXT);
                CREATE INDEX quota_recent ON quota_samples(kind, sampled_at);
                CREATE INDEX quota_scope_recent ON quota_samples(limit_id, kind, sampled_at);
                CREATE TABLE alert_events (
                    id INTEGER PRIMARY KEY, kind TEXT NOT NULL, threshold REAL,
                    event_type TEXT NOT NULL, event_at TEXT NOT NULL, detail TEXT NOT NULL);
                CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        ValidateExisting(path);
    }

    private static void ValidateExisting(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            connection.Open();
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"QuotaArc database integrity validation failed: {path}");
                }
            }

            foreach (var (table, requiredColumns) in RequiredColumns)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\");";
                using var reader = command.ExecuteReader();
                var actual = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read()) actual.Add(reader.GetString(1));
                if (requiredColumns.Any(column => !actual.Contains(column)))
                {
                    throw new InvalidDataException(
                        $"QuotaArc database schema is incomplete (table '{table}'): {path}");
                }
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException($"QuotaArc database is not a valid SQLite profile: {path}", exception);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void DeleteOwnedDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
