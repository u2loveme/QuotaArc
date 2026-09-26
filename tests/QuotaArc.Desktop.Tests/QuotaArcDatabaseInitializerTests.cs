using Microsoft.Data.Sqlite;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaArcDatabaseInitializerTests
{
    private static readonly string[] RequiredTables =
        ["alert_events", "app_settings", "import_state", "quota_samples", "usage_samples"];

    [Fact]
    public void FreshDatabaseHasCanonicalSchemaAndNoSeededProfileData()
    {
        using var profile = new TemporaryProfile();

        QuotaArcDatabaseInitializer.EnsureInitialized(profile.DatabasePath);

        Assert.True(File.Exists(profile.DatabasePath));
        using var connection = OpenReadOnly(profile.DatabasePath);
        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));
        Assert.Equal(RequiredTables, ReadNames(connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        Assert.Equal(
            ["quota_recent", "quota_scope_recent", "usage_by_day"],
            ReadNames(connection, "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        foreach (var table in RequiredTables)
        {
            Assert.Equal(0L, Scalar<long>(connection, $"SELECT COUNT(*) FROM \"{table}\";"));
        }
    }

    [Fact]
    public void ExistingDatabaseRowsAndSettingsArePreserved()
    {
        using var profile = new TemporaryProfile();
        QuotaArcDatabaseInitializer.EnsureInitialized(profile.DatabasePath);
        using (var connection = OpenReadWrite(profile.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO import_state VALUES ('session.jsonl', 42, '2026-09-25T00:00:00+00:00');
                INSERT INTO usage_samples VALUES ('sample', 'session', '2026-09-25T00:00:00+00:00', 12, 4, 2, 6, 0, 1000, 12);
                INSERT INTO quota_samples(kind, used_percent, sampled_at, source, limit_id)
                    VALUES ('5-hour', 25, '2026-09-25T00:00:00+00:00', 'APP_SERVER_LIVE', 'codex');
                INSERT INTO alert_events(kind, threshold, event_type, event_at, detail)
                    VALUES ('5-hour', 25, 'LOW_THRESHOLD', '2026-09-25T00:00:00+00:00', '{}');
                INSERT INTO app_settings VALUES ('history_daily_range', '14');
                """;
            command.ExecuteNonQuery();
        }

        QuotaArcDatabaseInitializer.EnsureInitialized(profile.DatabasePath);

        using var readOnly = OpenReadOnly(profile.DatabasePath);
        foreach (var table in RequiredTables) Assert.Equal(1L, Scalar<long>(readOnly, $"SELECT COUNT(*) FROM \"{table}\";"));
        Assert.Equal("14", Scalar<string>(readOnly,
            "SELECT value FROM app_settings WHERE key='history_daily_range';"));
        Assert.Equal(42L, Scalar<long>(readOnly, "SELECT offset FROM import_state WHERE path='session.jsonl';"));
    }

    [Theory]
    [InlineData("zero-byte")]
    [InlineData("partial-schema")]
    [InlineData("corrupt")]
    public void InvalidExistingDatabaseIsPreservedAndRejected(string kind)
    {
        using var profile = new TemporaryProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.DatabasePath)!);
        if (kind == "zero-byte") File.WriteAllBytes(profile.DatabasePath, []);
        else if (kind == "partial-schema")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = profile.DatabasePath,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }
        else File.WriteAllBytes(profile.DatabasePath, [0x51, 0x55, 0x4f, 0x54, 0x41]);
        var before = File.ReadAllBytes(profile.DatabasePath);

        Assert.Throws<InvalidDataException>(() => QuotaArcDatabaseInitializer.EnsureInitialized(profile.DatabasePath));

        Assert.Equal(before, File.ReadAllBytes(profile.DatabasePath));
    }

    [Fact]
    public void InitializedDatabaseSupportsDashboardReadsAndImporterAndLiveQuotaWrites()
    {
        using var profile = new TemporaryProfile();
        QuotaArcDatabaseInitializer.EnsureInitialized(profile.DatabasePath);
        var sessions = Path.Combine(profile.Root, "sessions");
        Directory.CreateDirectory(sessions);
        var sessionFile = Path.Combine(sessions, "session-a.jsonl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "token-events.jsonl"), sessionFile);

        var import = new CodexJsonlImporter().Import(profile.DatabasePath, [sessions]);
        Assert.Equal(4, import.EventsImported);
        Assert.Equal(4, import.QuotaSamplesImported);

        var sampledAt = DateTimeOffset.Parse("2026-09-26T09:00:00Z");
        using (var connection = OpenReadWrite(profile.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            QuotaAlertStore.StoreQuota(connection, transaction,
                new LiveQuotaSample("5-hour", "codex", 35, 300, sampledAt.AddHours(5), sampledAt),
                "APP_SERVER_LIVE", notify: true);
            transaction.Commit();
        }

        using (var connection = OpenReadOnly(profile.DatabasePath))
        {
            Assert.Equal(5L, Scalar<long>(connection, "SELECT COUNT(*) FROM quota_samples;"));
            Assert.Equal(4L, Scalar<long>(connection, "SELECT COUNT(*) FROM usage_samples;"));
            Assert.Equal("APP_SERVER_LIVE", Scalar<string>(connection,
                "SELECT source FROM quota_samples WHERE source='APP_SERVER_LIVE' LIMIT 1;"));
        }

        var dashboard = new SqliteTodayUsageReader().ReadDashboard(
            DateOnly.FromDateTime(DateTime.Now), databasePath: profile.DatabasePath);
        Assert.NotNull(dashboard);
    }

    [Fact]
    public void EnvironmentProfileRootsCanIsolateFirstRunFromDeveloperProfile()
    {
        using var profile = new TemporaryProfile();
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", profile.Root);
            Assert.Equal(Path.Combine(profile.Root, "QuotaArc", "telemetry.sqlite3"),
                SqliteTodayUsageReader.ProductionDatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
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

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string[] ReadNames(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private sealed class TemporaryProfile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"quotaarc-db-{Guid.NewGuid():N}");
        internal string DatabasePath => Path.Combine(Root, "QuotaArc", "telemetry.sqlite3");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
