using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal static class QuotaArcProfileMigrator
{
    private const string MarkerName = ".quotaarc-profile.json";
    private const string PartialProfileBackupPrefix = ".QuotaArc.pre-migration-";
    private static readonly JsonSerializerOptions StateJsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static string CanonicalDirectory => Path.Combine(
        QuotaArcProfilePaths.LocalAppDataRoot, "QuotaArc");

    internal static void EnsureCanonicalProfile()
        => EnsureCanonicalProfile(CanonicalDirectory, LegacyQuotaArcIdentity.DataDirectory);

    internal static void EnsureCanonicalProfile(string canonical, string legacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacy);
        canonical = Path.GetFullPath(canonical);
        legacy = Path.GetFullPath(legacy);
        if (File.Exists(Path.Combine(canonical, MarkerName))) return;
        var partialProfile = FindPartialCanonicalProfile(canonical);

        if (!Directory.Exists(legacy))
        {
            if (partialProfile is not null)
            {
                WriteMarker(partialProfile, migratedFrom: null);
                if (!Directory.Exists(canonical)) Directory.Move(partialProfile, canonical);
                return;
            }

            Directory.CreateDirectory(canonical);
            WriteMarker(canonical, migratedFrom: null);
            return;
        }

        var parent = Path.GetDirectoryName(canonical)!;
        var temporary = Path.Combine(parent, $".QuotaArc.migrating-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            CopyProfile(legacy, temporary);
            if (partialProfile is not null) MergePartialCanonicalState(partialProfile, temporary);
            WriteMarker(temporary, LegacyQuotaArcIdentity.DataDirectoryName);
            CommitProfile(temporary, canonical);
        }
        catch
        {
            // Keep the temporary copy for diagnosis and safe retry; neither source nor
            // canonical profile is removed on a failed validation.
            throw;
        }
    }

    internal static void RetireLegacyProfileAfterSuccessfulRead()
        => RetireLegacyProfileAfterSuccessfulRead(CanonicalDirectory, LegacyQuotaArcIdentity.DataDirectory);

    internal static void RetireLegacyProfileAfterSuccessfulRead(string canonical, string legacy)
    {
        canonical = Path.GetFullPath(canonical);
        legacy = Path.GetFullPath(legacy);
        if (!File.Exists(Path.Combine(canonical, MarkerName)) || !Directory.Exists(legacy)) return;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var retired = Path.Combine(Path.GetDirectoryName(legacy)!, LegacyQuotaArcIdentity.RetiredDataPrefix + stamp);
        Directory.Move(legacy, retired);
    }

    private static void CopyProfile(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("monitor.lock", StringComparison.OrdinalIgnoreCase)
                || name.Equals("monitor.ipc", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (IsSqliteFile(file))
            {
                BackupAndValidateSqlite(file, target);
            }
            else
            {
                File.Copy(file, target);
            }
        }
    }

    private static string? FindPartialCanonicalProfile(string canonical)
    {
        if (Directory.Exists(canonical))
        {
            ValidatePartialCanonicalProfile(canonical);
            return canonical;
        }

        var parent = Path.GetDirectoryName(canonical)!;
        if (!Directory.Exists(parent)) return null;
        var backup = Directory.EnumerateDirectories(parent, PartialProfileBackupPrefix + "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (backup is null) return null;
        ValidatePartialCanonicalProfile(backup);
        return backup;
    }

    private static void ValidatePartialCanonicalProfile(string directory)
    {
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "support-engagement-state.json",
            "wpf-window-positions.json"
        };
        var subdirectories = Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).ToArray();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        if (subdirectories.Length != 0 || files.Any(file => !allowedFiles.Contains(Path.GetFileName(file))))
        {
            throw new InvalidOperationException(
                $"QuotaArc profile exists without a validated migration marker and is not a recognized partial profile: {directory}");
        }

        foreach (var file in files)
        {
            if (Path.GetFileName(file).Equals("support-engagement-state.json", StringComparison.OrdinalIgnoreCase))
            {
                _ = ReadSupportEngagementState(file);
            }
            else
            {
                _ = ReadWindowPositions(file);
            }
        }
    }

    private static void MergePartialCanonicalState(string partialProfile, string candidate)
    {
        var partialSupport = Path.Combine(partialProfile, "support-engagement-state.json");
        var candidateSupport = Path.Combine(candidate, "support-engagement-state.json");
        if (File.Exists(partialSupport))
        {
            var partial = ReadSupportEngagementState(partialSupport);
            var legacy = File.Exists(candidateSupport)
                ? ReadSupportEngagementState(candidateSupport)
                : new SupportEngagementState();
            var firstLaunch = new[] { partial.FirstSuccessfulLaunchUtc, legacy.FirstSuccessfulLaunchUtc }
                .Where(value => value is not null)
                .Min();
            var merged = new SupportEngagementState(
                firstLaunch,
                Math.Max(partial.SuccessfulLaunchCount, legacy.SuccessfulLaunchCount),
                partial.HasObservedLiveQuotaData || legacy.HasObservedLiveQuotaData,
                partial.SupportPromptShown || legacy.SupportPromptShown,
                partial.SupportLinkOpened || legacy.SupportLinkOpened);
            File.WriteAllText(candidateSupport, JsonSerializer.Serialize(merged));
        }

        var partialPositions = Path.Combine(partialProfile, "wpf-window-positions.json");
        var candidatePositions = Path.Combine(candidate, "wpf-window-positions.json");
        if (!File.Exists(partialPositions)) return;
        var partialValues = ReadWindowPositions(partialPositions);
        var legacyValues = File.Exists(candidatePositions)
            ? ReadWindowPositions(candidatePositions)
            : new Dictionary<string, WindowPositionStore.SavedPosition>(StringComparer.Ordinal);
        var partialIsNewer = !File.Exists(candidatePositions)
            || File.GetLastWriteTimeUtc(partialPositions) >= File.GetLastWriteTimeUtc(candidatePositions);
        foreach (var (key, partialPosition) in partialValues)
        {
            if (!legacyValues.TryGetValue(key, out var legacyPosition))
            {
                legacyValues[key] = partialPosition;
                continue;
            }

            var partialVisible = WindowPositionStore.IsPositionOnVirtualScreen(partialPosition.Left, partialPosition.Top);
            var legacyVisible = WindowPositionStore.IsPositionOnVirtualScreen(legacyPosition.Left, legacyPosition.Top);
            if ((partialVisible && !legacyVisible)
                || (partialVisible == legacyVisible && partialIsNewer))
            {
                legacyValues[key] = partialPosition;
            }
        }

        File.WriteAllText(candidatePositions, JsonSerializer.Serialize(legacyValues));
    }

    private static SupportEngagementState ReadSupportEngagementState(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize<SupportEngagementState>(File.ReadAllText(path), StateJsonOptions)
                ?? throw new InvalidDataException($"Support state is empty: {path}");
            if (state.SuccessfulLaunchCount < 0)
            {
                throw new InvalidDataException($"Support state has an invalid launch count: {path}");
            }
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Support state is not valid JSON: {path}", exception);
        }
    }

    private static Dictionary<string, WindowPositionStore.SavedPosition> ReadWindowPositions(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, WindowPositionStore.SavedPosition>>(
                       File.ReadAllText(path), StateJsonOptions)
                   ?? throw new InvalidDataException($"Window positions are empty: {path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Window positions are not valid JSON: {path}", exception);
        }
    }

    private static void CommitProfile(string temporary, string canonical)
    {
        string? preservedPartial = null;
        if (Directory.Exists(canonical))
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
            preservedPartial = Path.Combine(Path.GetDirectoryName(canonical)!,
                PartialProfileBackupPrefix + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(canonical, preservedPartial);
        }

        try
        {
            Directory.Move(temporary, canonical);
        }
        catch
        {
            if (preservedPartial is not null && !Directory.Exists(canonical))
            {
                Directory.Move(preservedPartial, canonical);
            }
            throw;
        }
    }

    private static bool IsSqliteFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sqlite3" or ".sqlite" or ".db";

    private static void BackupAndValidateSqlite(string sourcePath, string targetPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        source.Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        target.Open();
        source.BackupDatabase(target);

        if (!string.Equals(ReadIntegrity(source), "ok", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ReadIntegrity(target), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity validation failed for '{sourcePath}'.");
        }

        var sourceCounts = ReadTableCounts(source);
        var targetCounts = ReadTableCounts(target);
        var expectedTables = new[] { "alert_events", "app_settings", "import_state", "quota_samples", "usage_samples" };
        if (sourceCounts.Count == 0 || sourceCounts.Count != targetCounts.Count
            || expectedTables.Any(table => !sourceCounts.ContainsKey(table))
            || sourceCounts.Any(pair => !targetCounts.TryGetValue(pair.Key, out var count) || count != pair.Value))
        {
            throw new InvalidDataException($"SQLite table/count validation failed for '{sourcePath}'.");
        }
    }

    private static string ReadIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    private static Dictionary<string, long> ReadTableCounts(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            counts.Add(table, (long)command.ExecuteScalar()!);
        }
        return counts;
    }

    private static void WriteMarker(string directory, string? migratedFrom)
    {
        var marker = new
        {
            schema_version = 1,
            created_at_utc = DateTimeOffset.UtcNow,
            migrated_from = migratedFrom,
            profile_validated = true
        };
        File.WriteAllText(Path.Combine(directory, MarkerName), JsonSerializer.Serialize(marker));
    }
}
