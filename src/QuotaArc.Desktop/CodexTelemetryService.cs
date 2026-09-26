using System.IO;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal sealed record DataRefreshResult(
    int UsageEventsImported,
    int QuotaSamplesImported,
    int LiveQuotasUpdated,
    bool AppServerAvailable,
    string? Error)
{
    public string Status => Error is not null
        ? $"Source refresh incomplete · {Error}"
        : AppServerAvailable
            ? $"Sources live · {UsageEventsImported} new usage events"
            : $"Usage source checked · live quota unavailable · {UsageEventsImported} new events";
}

internal sealed class CodexTelemetryService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly string _databasePath;
    private readonly string[] _roots;
    private readonly CodexJsonlImporter _importer = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _databaseWriteGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CodexAppServerClient _appServer;
    private Task? _loop;
    private int _disposed;

    public event Action<DataRefreshResult>? Refreshed;

    public CodexTelemetryService(string databasePath, IEnumerable<string> roots)
    {
        _databasePath = databasePath;
        _roots = roots.ToArray();
        _appServer = new CodexAppServerClient(OnPushedQuotaUpdate);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _loop ??= Task.Run(RunAsync);
    }

    public async Task<DataRefreshResult> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var imported = new CodexImportResult(0, 0);
            string? error = null;
            try
            {
                imported = await Task.Run(ImportSources, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or SqliteException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
            {
                error = "local usage import failed";
            }

            IReadOnlyList<LiveQuotaSample>? samples = null;
            var appServerAvailable = false;
            try
            {
                samples = await _appServer.RefreshAsync(linked.Token).ConfigureAwait(false);
                appServerAvailable = samples is { Count: > 0 };
                if (samples is not null)
                {
                    await Task.Run(() => StoreLiveQuotas(samples), linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException
                                       or OperationCanceledException or InvalidOperationException)
            {
                error ??= ex is OperationCanceledException ? "source refresh canceled" : "live quota refresh failed";
            }

            var result = new DataRefreshResult(imported.EventsImported, imported.QuotaSamplesImported,
                samples?.Count ?? 0, appServerAvailable, error);
            Refreshed?.Invoke(result);
            return result;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            await RefreshNowAsync(_stopping.Token).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await RefreshNowAsync(_stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private void OnPushedQuotaUpdate(IReadOnlyList<LiveQuotaSample> samples)
    {
        if (_stopping.IsCancellationRequested) return;
        _ = Task.Run(() =>
        {
            try { StoreLiveQuotas(samples); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { }
        }, _stopping.Token);
    }

    private void StoreLiveQuotas(IReadOnlyList<LiveQuotaSample> samples)
    {
        _databaseWriteGate.Wait();
        try
        {
            if (!File.Exists(_databasePath)) return;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var sample in samples)
            {
                QuotaAlertStore.StoreQuota(connection, transaction, sample, "APP_SERVER_LIVE", notify: sample.LimitId == "codex");
            }
            transaction.Commit();
        }
        finally
        {
            _databaseWriteGate.Release();
        }
    }

    private CodexImportResult ImportSources()
    {
        _databaseWriteGate.Wait();
        try { return _importer.Import(_databasePath, _roots); }
        finally { _databaseWriteGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException)) { }
        _appServer.Dispose();
        _stopping.Dispose();
        _refreshGate.Dispose();
        _databaseWriteGate.Dispose();
    }
}
