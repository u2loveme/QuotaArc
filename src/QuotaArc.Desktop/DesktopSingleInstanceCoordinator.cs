using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

namespace QuotaArc.Desktop;

public sealed class DesktopSingleInstanceCoordinator : IDisposable
{
    private const string ActivationMessage = "ACTIVATE_DASHBOARD\n";
    private readonly string _mutexName;
    private readonly string? _legacyMutexName;
    private readonly string _lockPath;
    private readonly string _activationPath;
    private readonly string? _legacyActivationPath;
    private readonly bool _migrateProfile;

    private Mutex? _mutex;
    private Mutex? _legacyMutex;
    private TcpListener? _listener;
    private Thread? _serverThread;
    private Action? _activate;
    private int _port;
    private bool _ownsMutex;
    private bool _ownsLegacyMutex;

    public DesktopSingleInstanceCoordinator()
        : this(
            @"Local\QuotaArc",
            LegacyQuotaArcIdentity.MutexName,
            QuotaArcProfileMigrator.CanonicalDirectory,
            LegacyQuotaArcIdentity.DataDirectory,
            migrateProfile: true)
    {
    }

    internal DesktopSingleInstanceCoordinator(string mutexName, string stateDirectory)
        : this(mutexName, null, stateDirectory, null, migrateProfile: false)
    {
    }

    internal DesktopSingleInstanceCoordinator(
        string mutexName,
        string? legacyMutexName,
        string stateDirectory,
        string? legacyStateDirectory,
        bool migrateProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _mutexName = mutexName;
        _legacyMutexName = legacyMutexName;
        _lockPath = Path.Combine(stateDirectory, "monitor.lock");
        _activationPath = Path.Combine(stateDirectory, "monitor.ipc");
        _legacyActivationPath = legacyStateDirectory is null
            ? null
            : Path.Combine(legacyStateDirectory, "monitor.ipc");
        _migrateProfile = migrateProfile;
    }

    public bool TryAcquire(Action activateExisting)
    {
        ArgumentNullException.ThrowIfNull(activateExisting);
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            SendActivationTo(_activationPath);
            return false;
        }

        _ownsMutex = true;
        if (_legacyMutexName is not null)
        {
            _legacyMutex = new Mutex(initiallyOwned: true, _legacyMutexName, out var legacyCreatedNew);
            if (!legacyCreatedNew)
            {
                _legacyMutex.Dispose();
                _legacyMutex = null;
                ReleaseOwnedMutexes();
                if (_legacyActivationPath is not null) SendActivationTo(_legacyActivationPath);
                return false;
            }
            _ownsLegacyMutex = true;
        }

        try
        {
            if (_migrateProfile) QuotaArcProfileMigrator.EnsureCanonicalProfile();
            Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        }
        catch
        {
            ReleaseOwnedMutexes();
            throw;
        }

        _activate = activateExisting;
        File.WriteAllText(_lockPath, $"{Environment.ProcessId}\n", Encoding.ASCII);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(2);
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        File.WriteAllText(_activationPath, _port.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.ASCII);
        _serverThread = new Thread(ActivationServerLoop)
        {
            IsBackground = true,
            Name = "QuotaArc-activation"
        };
        _serverThread.Start();
        return true;
    }

    public void Dispose()
    {
        if (_listener is not null)
        {
            _listener.Stop();
            _listener = null;
        }

        if (_serverThread is { IsAlive: true } && _serverThread != Thread.CurrentThread)
        {
            _serverThread.Join(TimeSpan.FromSeconds(1));
        }

        if (_port != 0 && File.Exists(_activationPath)
            && string.Equals(File.ReadAllText(_activationPath).Trim(),
                _port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            File.Delete(_activationPath);
        }

        ReleaseOwnedMutexes();
    }

    private void ActivationServerLoop()
    {
        while (_listener is not null)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = 1000;
                using var stream = client.GetStream();
                var buffer = new byte[64];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("ACTIVATE_DASHBOARD", StringComparison.Ordinal))
                {
                    _activate?.Invoke();
                }
            }
            catch (SocketException) when (_listener is null)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
            {
                // An incomplete local activation request is ignored; the server remains available.
            }
        }
    }

    private static void SendActivationTo(string activationPath)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var port = int.Parse(File.ReadAllText(activationPath, Encoding.ASCII).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
                using var client = new TcpClient();
                client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromMilliseconds(500));
                if (!client.Connected)
                {
                    Thread.Sleep(100);
                    continue;
                }

                using var stream = client.GetStream();
                var message = Encoding.ASCII.GetBytes(ActivationMessage);
                stream.Write(message, 0, message.Length);
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException
                                       or FormatException or ArgumentOutOfRangeException or AggregateException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void ReleaseOwnedMutexes()
    {
        if (_ownsLegacyMutex && _legacyMutex is not null)
        {
            _legacyMutex.ReleaseMutex();
            _ownsLegacyMutex = false;
        }
        _legacyMutex?.Dispose();
        _legacyMutex = null;

        if (_ownsMutex && _mutex is not null)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
