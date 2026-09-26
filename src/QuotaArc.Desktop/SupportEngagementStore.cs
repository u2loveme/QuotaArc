using System.IO;
using System.Text.Json;

namespace QuotaArc.Desktop;

internal sealed record SupportEngagementState(
    DateTimeOffset? FirstSuccessfulLaunchUtc = null,
    int SuccessfulLaunchCount = 0,
    bool HasObservedLiveQuotaData = false,
    bool SupportPromptShown = false,
    bool SupportLinkOpened = false);

internal readonly record struct SupportPromptConditions(
    bool HealthyLiveState,
    bool MainWindowActive,
    bool SessionReady,
    bool SessionStartedAfterMinimumAge,
    bool HistoryReady,
    bool ConflictingSurfaceOpen);

internal static class SupportPromptEligibility
{
    internal static bool IsEligible(
        SupportEngagementState state,
        DateTimeOffset now,
        SupportPromptConditions conditions) =>
        state.FirstSuccessfulLaunchUtc is { } firstLaunch
        && now - firstLaunch >= TimeSpan.FromHours(24)
        && state.SuccessfulLaunchCount >= 3
        && state.HasObservedLiveQuotaData
        && !state.SupportPromptShown
        && !state.SupportLinkOpened
        && conditions.HealthyLiveState
        && conditions.MainWindowActive
        && conditions.SessionReady
        && conditions.SessionStartedAfterMinimumAge
        && conditions.HistoryReady
        && !conditions.ConflictingSurfaceOpen;
}

internal sealed class SupportEngagementStore
{
    private readonly string _path;
    private SupportEngagementState _state;

    internal SupportEngagementStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            QuotaArcProfilePaths.LocalAppDataRoot,
            "QuotaArc",
            "support-engagement-state.json");
        _state = Load(_path);
    }

    internal SupportEngagementState State => _state;

    internal void RecordSuccessfulLaunch(DateTimeOffset now)
    {
        _state = _state with
        {
            FirstSuccessfulLaunchUtc = _state.FirstSuccessfulLaunchUtc ?? now,
            SuccessfulLaunchCount = Math.Max(0, _state.SuccessfulLaunchCount) + 1
        };
        Save();
    }

    internal void RecordLiveQuotaData()
    {
        if (_state.HasObservedLiveQuotaData) return;
        _state = _state with { HasObservedLiveQuotaData = true };
        Save();
    }

    internal void RecordPromptShown()
    {
        if (_state.SupportPromptShown) return;
        _state = _state with { SupportPromptShown = true };
        Save();
    }

    internal void RecordSupportLinkOpened()
    {
        if (_state.SupportLinkOpened) return;
        _state = _state with { SupportLinkOpened = true };
        Save();
    }

    private static SupportEngagementState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SupportEngagementState();
            return JsonSerializer.Deserialize<SupportEngagementState>(File.ReadAllText(path))
                ?? new SupportEngagementState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or ArgumentException or NotSupportedException)
        {
            return new SupportEngagementState();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or ArgumentException or NotSupportedException)
        {
            // Support prompts are optional; an unwritable state file must not affect dashboard use.
        }
    }
}
