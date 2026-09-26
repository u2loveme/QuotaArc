using Microsoft.Data.Sqlite;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaArcProfileMigratorTests
{
    [Fact]
    public void MigratesWalDatabaseAndProfileFilesIdempotentlyBeforeRetiringLegacy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-profile-{Guid.NewGuid():N}");
        var legacy = Path.Combine(directory, "CodexUsageMonitor");
        var canonical = Path.Combine(directory, "QuotaArc");
        Directory.CreateDirectory(legacy);
        var sourceDatabase = Path.Combine(legacy, "telemetry.sqlite3");
        var targetDatabase = Path.Combine(canonical, "telemetry.sqlite3");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={sourceDatabase};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE alert_events(id INTEGER PRIMARY KEY);
                    CREATE TABLE app_settings(id INTEGER PRIMARY KEY);
                    CREATE TABLE import_state(id INTEGER PRIMARY KEY);
                    CREATE TABLE quota_samples(id INTEGER PRIMARY KEY);
                    CREATE TABLE usage_samples(id INTEGER PRIMARY KEY);
                    INSERT INTO quota_samples VALUES (1);
                    INSERT INTO usage_samples VALUES (1);
                    """;
                command.ExecuteNonQuery();
                Assert.True(File.Exists(sourceDatabase + "-wal"));
                File.WriteAllText(Path.Combine(legacy, "support-engagement-state.json"), "{\"preserved\":true}");

                QuotaArcProfileMigrator.EnsureCanonicalProfile(canonical, legacy);

                using var migrated = new SqliteConnection($"Data Source={targetDatabase};Mode=ReadOnly;Pooling=False");
                migrated.Open();
                using var verify = migrated.CreateCommand();
                verify.CommandText = "SELECT COUNT(*) FROM quota_samples;";
                Assert.Equal(1L, (long)verify.ExecuteScalar()!);
                verify.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", verify.ExecuteScalar());
            }

            Assert.Equal("{\"preserved\":true}", File.ReadAllText(Path.Combine(canonical, "support-engagement-state.json")));
            Assert.True(File.Exists(Path.Combine(canonical, ".quotaarc-profile.json")));
            Assert.True(Directory.Exists(legacy));

            QuotaArcProfileMigrator.EnsureCanonicalProfile(canonical, legacy);
            QuotaArcProfileMigrator.RetireLegacyProfileAfterSuccessfulRead(canonical, legacy);

            Assert.False(Directory.Exists(legacy));
            Assert.Single(Directory.GetDirectories(directory, "CodexUsageMonitor.migrated-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RefusesTwoUnmarkedProfilesWithoutChangingEither()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-conflict-{Guid.NewGuid():N}");
        var legacy = Path.Combine(directory, "CodexUsageMonitor");
        var canonical = Path.Combine(directory, "QuotaArc");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(legacy, "legacy.txt"), "legacy");
        File.WriteAllText(Path.Combine(canonical, "canonical.txt"), "canonical");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                QuotaArcProfileMigrator.EnsureCanonicalProfile(canonical, legacy));
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "legacy.txt")));
            Assert.Equal("canonical", File.ReadAllText(Path.Combine(canonical, "canonical.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MergesRecognizedPartialQuotaArcStateWithoutDroppingEitherProfilesValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quotaarc-partial-{Guid.NewGuid():N}");
        var legacy = Path.Combine(directory, "CodexUsageMonitor");
        var canonical = Path.Combine(directory, "QuotaArc");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(canonical);
        var database = Path.Combine(legacy, "telemetry.sqlite3");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE alert_events(id INTEGER PRIMARY KEY);
                    CREATE TABLE app_settings(id INTEGER PRIMARY KEY);
                    CREATE TABLE import_state(id INTEGER PRIMARY KEY);
                    CREATE TABLE quota_samples(id INTEGER PRIMARY KEY);
                    CREATE TABLE usage_samples(id INTEGER PRIMARY KEY);
                    INSERT INTO quota_samples VALUES (1);
                    """;
                command.ExecuteNonQuery();
            }

            File.WriteAllText(Path.Combine(legacy, "support-engagement-state.json"), """
                {"FirstSuccessfulLaunchUtc":"2026-09-24T10:00:00Z","SuccessfulLaunchCount":16,"HasObservedLiveQuotaData":true,"SupportPromptShown":false,"SupportLinkOpened":true}
                """);
            File.WriteAllText(Path.Combine(canonical, "support-engagement-state.json"), """
                {"FirstSuccessfulLaunchUtc":null,"SuccessfulLaunchCount":0,"HasObservedLiveQuotaData":true,"SupportPromptShown":false,"SupportLinkOpened":false}
                """);
            File.WriteAllText(Path.Combine(legacy, "wpf-window-positions.json"), """
                {"main":{"Left":156,"Top":156},"compact":{"Left":11,"Top":926,"Width":220,"Height":56}}
                """);
            File.WriteAllText(Path.Combine(canonical, "wpf-window-positions.json"), """
                {"main":{"Left":-5000,"Top":-5000}}
                """);

            QuotaArcProfileMigrator.EnsureCanonicalProfile(canonical, legacy);

            var migratedSupport = System.Text.Json.JsonSerializer.Deserialize<SupportEngagementState>(
                File.ReadAllText(Path.Combine(canonical, "support-engagement-state.json")));
            Assert.NotNull(migratedSupport);
            Assert.Equal(DateTimeOffset.Parse("2026-09-24T10:00:00Z"), migratedSupport.FirstSuccessfulLaunchUtc);
            Assert.Equal(16, migratedSupport.SuccessfulLaunchCount);
            Assert.True(migratedSupport.HasObservedLiveQuotaData);
            Assert.True(migratedSupport.SupportLinkOpened);

            var migratedPositions = System.Text.Json.JsonSerializer.Deserialize<
                Dictionary<string, WindowPositionStore.SavedPosition>>(
                File.ReadAllText(Path.Combine(canonical, "wpf-window-positions.json")));
            Assert.NotNull(migratedPositions);
            Assert.Equal(new System.Windows.Point(156, 156),
                new System.Windows.Point(migratedPositions["main"].Left, migratedPositions["main"].Top));
            Assert.Equal(new System.Windows.Point(11, 926),
                new System.Windows.Point(migratedPositions["compact"].Left, migratedPositions["compact"].Top));
            Assert.Equal(1L, CountRows(Path.Combine(canonical, "telemetry.sqlite3"), "quota_samples"));
            Assert.Equal("{\"FirstSuccessfulLaunchUtc\":null,\"SuccessfulLaunchCount\":0,\"HasObservedLiveQuotaData\":true,\"SupportPromptShown\":false,\"SupportLinkOpened\":false}",
                File.ReadAllText(Directory.GetDirectories(directory, ".QuotaArc.pre-migration-*" )[0] + Path.DirectorySeparatorChar + "support-engagement-state.json"));
            Assert.True(Directory.Exists(legacy));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static long CountRows(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return (long)command.ExecuteScalar()!;
    }
}
