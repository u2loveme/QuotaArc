# Quota alert semantics

Quota alerts are severity levels, not a checklist of every threshold crossed.
For one quota source and one accepted sample, the alert store consumes every
newly crossed threshold but records at most one `LOW_THRESHOLD` event: the
most severe newly reached level. A valid 0% sample is the terminal level and
uses threshold `0`; repeated 0% samples do not create another event.

The first valid sample for a quota source establishes a baseline and emits no
historical catch-up notifications. This prevents a restart or a newly created
profile from replaying old 25%, 15%, 10%, and 5% states. The consumed levels
are persisted in `app_settings` as part of `quota_state.<kind>`, so restarting
the app does not re-arm the current cycle.

Thresholds are re-armed only when the sample carries a genuinely newer reset
identity, or when the stored reset boundary is crossed in sample time. A
temporary increase such as `0% → 100%` with the same reset identity is treated
as a recovery and does not re-arm alerts. Samples older than the latest accepted
live sample are ignored for notification evaluation.

Windows notifications use the stable `quota-alerts` group and one tag per
quota source (`5-hour-quota`, `weekly-quota`, or `reserve-quota`) as a second
deduplication layer. This does not replace the persisted threshold semantics;
it only prevents stale cards for one source from accumulating in Notification
Center.
