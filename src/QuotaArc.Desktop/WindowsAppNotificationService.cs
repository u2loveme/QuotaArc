using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace QuotaArc.Desktop;

public sealed class WindowsAppNotificationService : IDisposable
{
    private readonly AppNotificationManager? _manager;
    private bool _registered;

    public WindowsAppNotificationService(Action<string>? notificationActivated = null)
    {
        if (notificationActivated is not null)
        {
            NotificationActivated += notificationActivated;
        }

        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            Status = GetSettingStatus(_manager.Setting);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or UnauthorizedAccessException
                                   or System.Runtime.InteropServices.COMException
                                   or DllNotFoundException
                                   or TypeInitializationException)
        {
            Status = "Windows notifications are unavailable";
            if (_manager is not null)
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
            }
        }
    }

    public event Action<string>? NotificationActivated;

    public string Status { get; private set; }

    public bool AreEnabled
    {
        get
        {
            if (!_registered || _manager is null)
            {
                return false;
            }

            try
            {
                Status = GetSettingStatus(_manager.Setting);
                return _manager.Setting == AppNotificationSetting.Enabled;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException
                                       or DllNotFoundException
                                       or TypeInitializationException)
            {
                Status = "Windows notifications are unavailable";
                return false;
            }
        }
    }

    public bool ShowAlert(AlertEventSnapshot alert, bool suppressDisplay) =>
        ShowNotification(
            "alertId",
            alert.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            alert.Message,
            suppressDisplay);

    public bool ShowTestNotification() =>
        ShowNotification("test", "1", "Windows notification delivery is enabled.", suppressDisplay: false);

    private bool ShowNotification(
        string argumentName,
        string argumentValue,
        string message,
        bool suppressDisplay)
    {
        if (!AreEnabled || _manager is null)
        {
            return false;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument(argumentName, argumentValue)
                .AddText("QuotaArc")
                .AddText(message)
                .BuildNotification();
            notification.SuppressDisplay = suppressDisplay;
            _manager.Show(notification);
            Status = GetSettingStatus(_manager.Setting);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or UnauthorizedAccessException
                                   or System.Runtime.InteropServices.COMException
                                   or DllNotFoundException)
        {
            Status = "Windows could not display notifications";
            return false;
        }
    }

    public void Dispose()
    {
        if (!_registered || _manager is null)
        {
            return;
        }

        _manager.NotificationInvoked -= OnNotificationInvoked;
        try
        {
            _manager.Unregister();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.Runtime.InteropServices.COMException
                                   or DllNotFoundException)
        {
            // Keep shutdown reliable when notification support disappears during the session.
        }

        _registered = false;
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) =>
        NotificationActivated?.Invoke(args.Argument);

    private static string GetSettingStatus(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => "Windows notifications are on",
        AppNotificationSetting.DisabledForApplication => "Notifications are disabled for QuotaArc in Windows",
        AppNotificationSetting.DisabledForUser => "Windows notifications are disabled for this user",
        AppNotificationSetting.DisabledByGroupPolicy => "Windows notifications are disabled by system policy",
        AppNotificationSetting.DisabledByManifest => "Windows notifications are unavailable for this app",
        _ => "Windows notifications are unsupported on this system"
    };
}
