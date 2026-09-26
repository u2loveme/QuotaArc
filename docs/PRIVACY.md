# Privacy

QuotaArc is a local-first Windows desktop application.

## Desktop application

- For live quota status, QuotaArc starts the user's discoverable Codex CLI as
  `codex app-server --stdio` and requests quota information through that local
  process. QuotaArc has no separate OpenAI sign-in flow and does not copy or
  store Codex credentials such as `auth.json`.
- QuotaArc scans the local Codex `sessions` and `archived_sessions` JSONL files
  to find token-count events. It imports numeric usage and quota fields and
  does not retain source records, prompts, responses, or tool output.
- Local SQLite data includes imported usage/quota samples, session and source
  file identities/positions needed for incremental import, alerts, and app
  preferences. It is stored under `%LOCALAPPDATA%\QuotaArc` and is not sent by
  QuotaArc to Vivi Bureau.
- The desktop app has no launch or download analytics integration. It opens
  the Vivi Bureau support page only when the user selects a support link.

## Vivi Bureau website

The website is a separate service. Its download route may record download
attribution/analytics events according to the website's own configuration.
Those website events are separate from data stored by the desktop application.

## Local data lifecycle

Uninstalling QuotaArc removes its managed application files and shortcut. The
installer intentionally retains the local database and preferences so a later
reinstall does not erase usage history. Users can remove `%LOCALAPPDATA%\QuotaArc`
themselves if they want to delete that local data.
