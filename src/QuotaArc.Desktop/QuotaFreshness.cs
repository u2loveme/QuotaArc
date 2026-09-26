namespace QuotaArc.Desktop;

internal enum QuotaSampleState
{
    Unavailable,
    Current,
    Stale,
    Offline
}

internal sealed record QuotaUpdateStatus(string Text, QuotaSampleState State);

internal static class QuotaFreshness
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LiveAfter = TimeSpan.FromMinutes(2);

    internal static TimeSpan Age(QuotaUsageSnapshot quota, DateTimeOffset now) => now - quota.SampledAt;

    internal static QuotaSampleState GetState(QuotaUsageSnapshot? quota, DateTimeOffset now)
    {
        if (quota is null)
        {
            return QuotaSampleState.Unavailable;
        }

        var age = Age(quota, now);
        return age > StaleAfter || age < TimeSpan.Zero
            ? QuotaSampleState.Stale
            : QuotaSampleState.Current;
    }

    internal static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalDays >= 1)
        {
            return $"{(int)age.TotalDays}d {age.Hours}h";
        }
        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        }
        return $"{Math.Max(0, age.Minutes)}m";
    }

    internal static QuotaUpdateStatus GetUpdateStatus(
        QuotaUsageSnapshot? quota,
        DateTimeOffset now,
        bool allQuotasFromLiveSource,
        bool liveSourceAvailable)
    {
        if (quota is null)
        {
            return liveSourceAvailable
                ? new("Unavailable · No quota sample", QuotaSampleState.Unavailable)
                : new("Offline · No quota sample", QuotaSampleState.Offline);
        }

        var age = Age(quota, now);
        if (age < TimeSpan.Zero)
        {
            return new(liveSourceAvailable
                ? "Clock mismatch · Sample time ahead"
                : "Offline · Clock mismatch · Sample time ahead", liveSourceAvailable
                    ? QuotaSampleState.Stale
                    : QuotaSampleState.Offline);
        }

        var state = GetState(quota, now);
        if (!liveSourceAvailable)
        {
            var ageLabel = state == QuotaSampleState.Stale ? "Stale · " : string.Empty;
            return new($"Offline · {ageLabel}Updated {FormatAge(age)}", QuotaSampleState.Offline);
        }

        var label = state == QuotaSampleState.Stale
            ? "Stale"
            : allQuotasFromLiveSource && age <= LiveAfter ? "Live" : "Recent";
        return new($"{label} · Updated {FormatAge(age)}", state);
    }

    internal static string FormatSampleDetail(QuotaUsageSnapshot quota, DateTimeOffset now, bool liveSourceAvailable)
    {
        if (Age(quota, now) < TimeSpan.Zero)
        {
            return liveSourceAvailable
                ? "Clock mismatch · sample time is ahead"
                : "Offline · Clock mismatch · sample time is ahead";
        }

        var state = GetState(quota, now);
        var age = FormatAge(Age(quota, now));
        var ageText = state == QuotaSampleState.Stale ? $"Stale · Last update {age} ago" : $"Updated {age} ago";
        return liveSourceAvailable ? ageText : $"Offline · {ageText}";
    }
}
