using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace QuotaArc.Desktop;

internal sealed record LiveQuotaSample(
    string Kind,
    string LimitId,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetAt,
    DateTimeOffset SampledAt);

internal sealed class CodexAppServerClient : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Action<IReadOnlyList<LiveQuotaSample>> _onQuotaUpdate;
    private readonly CancellationTokenSource _stopping = new();
    private Process? _process;
    private Task? _readTask;
    private Task? _stderrTask;
    private long _nextId;

    public CodexAppServerClient(Action<IReadOnlyList<LiveQuotaSample>> onQuotaUpdate) =>
        _onQuotaUpdate = onQuotaUpdate;

    public async Task<IReadOnlyList<LiveQuotaSample>?> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureStartedAsync(cancellationToken).ConfigureAwait(false)) return null;
        using var response = await RequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var body = response.RootElement;
        var result = TryProperty(body, "result", out var resultValue) ? resultValue : body;
        return ParseQuotas(result, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<LiveQuotaSample>? ParseQuotas(JsonElement result, DateTimeOffset sampledAt)
    {
        JsonElement byId = default;
        var hasById = TryProperty(result, "rateLimitsByLimitId", out byId)
            || TryProperty(result, "rate_limits_by_limit_id", out byId);
        var codex = hasById && TryProperty(byId, "codex", out var codexValue)
            ? codexValue
            : default;
        if (codex.ValueKind != JsonValueKind.Object)
        {
            if ((!TryProperty(result, "rateLimits", out codex) && !TryProperty(result, "rate_limits", out codex))
                || !TryStringEither(codex, "limitId", "limit_id", out var limitId)
                || limitId != "codex")
            {
                codex = default;
            }
        }

        var samples = new List<LiveQuotaSample>();
        if (codex.ValueKind == JsonValueKind.Object)
        {
            AddItem(codex, "primary", "5-hour", "codex", 300, sampledAt, samples);
            AddItem(codex, "secondary", "weekly", "codex", 10080, sampledAt, samples);
        }

        var reserve = hasById && TryProperty(byId, "base_model_inference", out var reserveValue)
            ? reserveValue
            : default;
        if (reserve.ValueKind == JsonValueKind.Object)
        {
            if (TryProperty(reserve, "primary", out var primary) && TryUsed(primary, out _))
            {
                AddItem(reserve, "primary", "reserve", "base_model_inference", 10080, sampledAt, samples);
            }
            else
            {
                AddItem(reserve, "secondary", "reserve", "base_model_inference", 10080, sampledAt, samples);
            }
        }

        return samples.Count == 0 ? null : samples;
    }

    private async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return true;
        await StopProcessAsync().ConfigureAwait(false);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        try
        {
            startInfo.FileName = ResolveCodexCommand();
            _process = Process.Start(startInfo);
            if (_process is null) return false;
            _readTask = ReadLoopAsync(_process, _stopping.Token);
            _stderrTask = _process.StandardError.ReadToEndAsync(_stopping.Token);
            using var response = await RequestAsync("initialize", new
            {
                clientInfo = new { name = "quotaarc-desktop", version = "0.9.0" }
            }, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                await StopProcessAsync().ConfigureAwait(false);
                return false;
            }
            await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception
                                   or OperationCanceledException or TimeoutException)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task<JsonDocument?> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await SendAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited) throw new IOException("Codex App Server is not running.");
        var json = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                JsonDocument? document = null;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                var root = document.RootElement;
                if (TryProperty(root, "id", out var idValue) && idValue.TryGetInt64(out var id)
                    && _pending.TryGetValue(id, out var pending))
                {
                    if (!pending.TrySetResult(document)) document.Dispose();
                    continue;
                }

                if (TryString(root, "method", out var method) && method == "account/rateLimits/updated"
                    && TryProperty(root, "params", out var parameters))
                {
                    var samples = ParseQuotas(parameters, DateTimeOffset.UtcNow);
                    if (samples is not null) _onQuotaUpdate(samples);
                }
                document.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetResult(null);
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string ResolveCodexCommand()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex"))
            .FirstOrDefault(File.Exists);
        if (path is not null) return path;

        var localAppData = QuotaArcProfilePaths.LocalAppDataRoot;
        var installRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            var installed = Directory.EnumerateDirectories(installRoot)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (installed is not null) return installed;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return OperatingSystem.IsWindows() ? "codex.exe" : "codex";
    }

    private static void AddItem(JsonElement bucket, string field, string fallbackKind, string limitId,
        int expectedWindow, DateTimeOffset sampledAt, ICollection<LiveQuotaSample> output)
    {
        if (!TryProperty(bucket, field, out var item) || !TryUsed(item, out var used)) return;
        var window = TryDoubleEither(item, "windowDurationMins", "window_minutes", out var minutes)
            && double.IsFinite(minutes) && minutes >= int.MinValue && minutes <= int.MaxValue
            ? (int)minutes
            : expectedWindow;
        if (window != expectedWindow) return;
        DateTimeOffset? resetAt = TryDoubleEither(item, "resetsAt", "resets_at", out var resetSeconds)
            ? FromUnixSeconds(resetSeconds)
            : null;
        output.Add(new LiveQuotaSample(fallbackKind, limitId, used, window, resetAt, sampledAt));
    }

    private static bool TryUsed(JsonElement item, out double used) =>
        TryDoubleEither(item, "usedPercent", "used_percent", out used) && double.IsFinite(used);

    private static DateTimeOffset? FromUnixSeconds(double value)
    {
        if (!double.IsFinite(value)) return null;
        try { return DateTimeOffset.UnixEpoch.AddSeconds(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryStringEither(JsonElement element, string first, string second, out string value) =>
        TryString(element, first, out value) || TryString(element, second, out value);

    private static bool TryDoubleEither(JsonElement element, string first, string second, out double value)
    {
        value = 0;
        return (TryProperty(element, first, out var property) || TryProperty(element, second, out property))
            && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        StopProcessAsync().GetAwaiter().GetResult();
        _stopping.Dispose();
        _writeLock.Dispose();
        _requestLock.Dispose();
    }
}
