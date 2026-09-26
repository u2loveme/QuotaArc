# Data sources

## Live quota

QuotaArc starts the discoverable Codex CLI as `codex app-server --stdio` and
requests account rate limits through its local standard-input/output protocol.
The Codex CLI must be installed, signed in, and able to reach the Codex service
for live quota data. If the process is unavailable or unauthenticated, the UI
reports quota as unavailable instead of estimating it.

## Local token history

Read-only source folders:

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`

The importer selects JSONL events where `payload.type` is `token_count`. It
uses `payload.info.total_token_usage`, `last_token_usage`, and
`model_context_window`, plus quota fields when present. It stores normalized
usage/quota samples and an incremental file offset; it does not store complete
source records.

The database is local to `%LOCALAPPDATA%\QuotaArc\telemetry.sqlite3`. Token
totals represent locally observed usage, not an account-wide billing total.
