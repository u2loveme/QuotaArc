using System.IO;

namespace QuotaArc.Desktop;

internal static class LegacyQuotaArcIdentity
{
    internal const string ProductName = "Codex Usage Monitor";
    internal const string PythonModule = "codex_usage_monitor";
    internal const string DesktopNamespace = "TokenMonitor.Desktop";
    internal const string DesktopExecutable = "TokenMonitor.Desktop.exe";
    internal const string ManagedInstallMarker = ".token-monitor-managed";
    internal const string MutexName = @"Local\CodexUsageMonitor";
    internal const string DataDirectoryName = "CodexUsageMonitor";
    internal const string RetiredDataPrefix = "CodexUsageMonitor.migrated-";
    internal const string InstallDirectoryName = "Codex Usage Monitor";
    internal const string ShortcutName = "Codex Usage Monitor.lnk";

    internal static string DataDirectory => Path.Combine(
        QuotaArcProfilePaths.LocalAppDataRoot, DataDirectoryName);

    internal static string InstallDirectory => Path.Combine(
        QuotaArcProfilePaths.LocalAppDataRoot, "Programs", InstallDirectoryName);
}
