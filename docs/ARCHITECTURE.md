# Architecture

The current QuotaArc desktop application is a Windows Presentation Foundation
(WPF) application targeting `net10.0-windows10.0.19041.0` and `win-x64`.

## Startup and local state

`App` acquires the single-instance coordinator, initializes or validates the
local SQLite database, wires Windows notifications, and opens the main window.
The database and user preferences live under `%LOCALAPPDATA%\QuotaArc`.
First-run database initialization does not require Python or Codex.

## Quota and usage sources

`CodexAppServerClient` discovers the Codex CLI and runs
`codex app-server --stdio`. Quota requests and responses use that local
standard-input/output connection. QuotaArc does not implement its own account
authentication flow.

`CodexTelemetryService` also watches the local Codex `sessions` and
`archived_sessions` directories. `CodexJsonlImporter` selects token-count
events and stores the required numeric usage/quota values and incremental
import state in SQLite. It does not retain complete source JSONL records.

## UI and platform services

The WPF main window presents quota status, history, token usage, alerts, and
the compact Mini panel. Windows App SDK APIs provide app notifications. The
app uses local timers and the tray integration; it does not run a cloud service
or upload usage history.

## Distribution

The release package is published self-contained for Windows x64 and includes
Windows App SDK dependencies. The PowerShell installer places the app under
`%LOCALAPPDATA%\Programs\QuotaArc`, creates a desktop shortcut, and preserves
local user data when uninstalling.

The public beta source covers the current WPF application. Retired prototypes
and internal refactoring experiments are intentionally outside that source
snapshot.
