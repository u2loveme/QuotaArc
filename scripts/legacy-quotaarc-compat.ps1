# Temporary upgrade identifiers, shared by package installation and removal.
$script:LegacyQuotaArc = @{
    Product = 'Codex Usage Monitor'
    PythonModule = 'codex_usage_monitor'
    DesktopNamespace = 'TokenMonitor.Desktop'
    Executable = 'TokenMonitor.Desktop.exe'
    InstallDirectory = 'Codex Usage Monitor'
    DataDirectory = 'CodexUsageMonitor'
    ManagedMarker = '.token-monitor-managed'
    ManagedMarkerValue = 'Codex Usage Monitor managed install'
    Shortcut = 'Codex Usage Monitor.lnk'
    Mutex = 'Local\CodexUsageMonitor'
}
