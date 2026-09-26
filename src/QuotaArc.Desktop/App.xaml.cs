using System.IO;
using System.Windows;

namespace QuotaArc.Desktop;

internal readonly record struct NotificationActivationRequest(bool IsTest, long? AlertId);

public partial class App : Application
{
    private DesktopSingleInstanceCoordinator? _instanceCoordinator;
    private WindowsAppNotificationService? _notificationService;
    private CodexTelemetryService? _telemetryService;
    private MainWindow? _mainWindow;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _instanceCoordinator = new DesktopSingleInstanceCoordinator();
        if (!_instanceCoordinator.TryAcquire(() => Dispatcher.BeginInvoke(() => _mainWindow?.ActivateFromExistingInstance())))
        {
            Shutdown();
            return;
        }

        QuotaArcDatabaseInitializer.EnsureInitialized(SqliteTodayUsageReader.ProductionDatabasePath);
        _notificationService = new WindowsAppNotificationService(NotificationService_NotificationActivated);
        var userProfile = QuotaArcProfilePaths.UserProfileRoot;
        _telemetryService = new CodexTelemetryService(
            SqliteTodayUsageReader.ProductionDatabasePath,
            [Path.Combine(userProfile, ".codex", "sessions"), Path.Combine(userProfile, ".codex", "archived_sessions")]);
        _mainWindow = new MainWindow(_notificationService, _telemetryService);
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    private void NotificationService_NotificationActivated(string arguments)
    {
        var activation = ParseNotificationActivation(arguments);
        if (activation.IsTest)
        {
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.ActivateFromTestNotification());
            return;
        }

        _ = Dispatcher.BeginInvoke(() => _mainWindow?.ActivateFromNotification(activation.AlertId));
    }

    internal static NotificationActivationRequest ParseNotificationActivation(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => string.Equals(argument, "test=1", StringComparison.Ordinal)))
        {
            return new NotificationActivationRequest(IsTest: true, AlertId: null);
        }

        return new NotificationActivationRequest(IsTest: false, AlertId: ParseAlertId(arguments));
    }

    private static long? ParseAlertId(string arguments)
    {
        foreach (var argument in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = argument.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "alertId"
                && long.TryParse(pair[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var alertId))
            {
                return alertId;
            }
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _telemetryService?.Dispose();
        if (_notificationService is not null)
        {
            _notificationService.Dispose();
        }
        _instanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
