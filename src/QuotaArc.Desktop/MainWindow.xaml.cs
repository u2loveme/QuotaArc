using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace QuotaArc.Desktop;

internal sealed record QuotaGapBridge(QuotaHistoryPoint From, QuotaHistoryPoint To);
internal sealed record QuotaCycleBar(
    DateTimeOffset CycleAt,
    double UsedPercent,
    int SampleCount,
    DateTimeOffset FirstSampleAt,
    DateTimeOffset LastSampleAt);
internal readonly record struct QuotaChartScale(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Bottom => Top + Height;
    public double DataTop => Top + 8;
    public double DataBottom => Bottom - 8;

    public double MapX(DateTimeOffset sampledAt) =>
        Left + (sampledAt - StartAt).TotalSeconds
        / Math.Max(1, (EndAt - StartAt).TotalSeconds) * Width;

    public double MapY(double percent) =>
        DataTop + (DataBottom - DataTop) * (100 - Math.Clamp(percent, 0, 100)) / 100.0;

    public Point Map(DateTimeOffset sampledAt, double percent) =>
        new(MapX(sampledAt), MapY(percent));

    public DateTimeOffset TimeAt(double x) =>
        StartAt.AddSeconds((EndAt - StartAt).TotalSeconds
            * Math.Clamp((x - Left) / Math.Max(1, Width), 0, 1));
}
internal sealed record QuotaResetEventGroup(DateTimeOffset EventAt, IReadOnlyList<string> Kinds);
internal sealed record NotificationFeedItem(
    long LatestAlertId,
    IReadOnlyList<long> AlertIds,
    string Kind,
    string Message,
    string Category,
    DateTimeOffset EventAt,
    Brush Accent,
    bool IsUnread,
    int Count,
    bool IsFocused)
{
    public string AutomationLabel =>
        $"{Category}. {Message}. {Count} event{(Count == 1 ? string.Empty : "s")}. "
        + $"{EventAt.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture)}. "
        + (IsUnread ? "Unread" : "Read");
}

internal readonly record struct MaximizedWorkAreaBounds(int X, int Y, int Width, int Height);

public partial class MainWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmCaptionColor = 35;
    private const int DwmBorderColor = 34;
    private const int DwmTextColor = 36;
    private const double MinVisibleQuotaStaleTail = 12;
    private static readonly TimeSpan MaximumQuotaGap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuotaSourceDuplicateWindow = TimeSpan.FromSeconds(30);
    private const string LiveQuotaSource = "APP_SERVER_LIVE";
    private const string ImportedQuotaSource = "LOCAL_CONFIRMED_CODEX_JSONL";
    internal const string SupportHubUrl = "https://vivibureau.pp.ua/quotaarc";
    private static readonly TimeSpan SupportPromptActiveDelay = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> NonDraggableRegions = new(StringComparer.Ordinal)
    {
        "FiveHourCard", "WeeklyCard", "ReserveCard", "TodayCard",
        "QuotaHistoryCard", "DailyHistoryCard", "QuotaChart", "DailyChart",
        "DailyHeatmap", "QuotaRangePanel", "DailyRangePanel"
    };

    private readonly SqliteTodayUsageReader _usageReader = new();
    private readonly SupportEngagementStore _supportEngagement = new();
    private readonly WindowsAppNotificationService? _notificationService;
    private readonly CodexTelemetryService? _telemetryService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _notificationRefreshTimer;
    private TrayIconController? _trayIcon;
    private CompactWindow? _compactWindow;
    private HwndSource? _windowSource;
    private IReadOnlyList<AlertEventSnapshot> _alertEvents = [];
    private string _notificationDeliveryMode = "quiet";
    private string _notificationBadgeMode = "dot";
    private string _notificationSoundMode = "off";
    private bool _notificationBaselineInitialized;
    private long _lastSeenAlertEventId;
    private long? _notificationEventToFocus;
    private DashboardUsageSnapshot? _snapshot;
    private IReadOnlyList<DailyUsageSnapshot> _history = [];
    private IReadOnlyList<QuotaHistoryPoint> _visibleQuotaHistory = [];
    private IReadOnlyList<QuotaHistoryPoint> _visibleFiveHourHistory = [];
    private IReadOnlyList<QuotaHistoryPoint> _visibleWeeklyHistory = [];
    private IReadOnlyList<QuotaResetEventSnapshot> _visibleQuotaResetEvents = [];
    private Line? _quotaCrosshair;
    private Ellipse? _quotaFiveHourInspectionMarker;
    private Ellipse? _quotaWeeklyInspectionMarker;
    private Border? _quotaInspectionTooltip;
    private TextBlock? _quotaInspectionTooltipText;
    private DateOnly? _selectedHistoryDay;
    private DateTimeOffset? _lastHistoryReadAt;
    private int _historyDays = 14;
    private int _quotaHours = 24;
    private bool _showFiveHourHistory = true;
    private bool _showWeeklyHistory = true;
    private bool _rangePreferencesLoaded;
    private bool _refreshInProgress;
    private bool _manualRefreshInProgress;
    private bool _historyRefreshRequested;
    private bool _historyRangeSaveInProgress;
    private bool _quotaChartNeedsRender = true;
    private bool _dailyChartNeedsRender = true;
    private bool _quotaSourceUnavailable;
    private bool _successfulLaunchRecorded;
    private DateTimeOffset? _supportActiveSince;
    private string _sourceRefreshStatus = "local database only";

    internal MainWindow(WindowsAppNotificationService? notificationService = null, CodexTelemetryService? telemetryService = null)
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeRestoreGlyph();
        UpdateMaximizeRestoreGlyph();
        _notificationService = notificationService;
        _telemetryService = telemetryService;
        if (_telemetryService is not null) _telemetryService.Refreshed += TelemetryService_Refreshed;
        // Place relative to the full-width header so the popup's right edge tracks the window edge.
        NotificationPopup.PlacementTarget = HeaderBar;
        NotificationPopup.CustomPopupPlacementCallback = PlaceNotificationPopup;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _notificationRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _notificationRefreshTimer.Tick += NotificationRefreshTimer_Tick;
        Closed += (_, _) => _refreshTimer.Stop();
        Closed += (_, _) => _notificationRefreshTimer.Stop();
        Closed += (_, _) => SupportPromptPopup.Visibility = Visibility.Collapsed;
        Closed += (_, _) =>
        {
            SaveNormalWindowPosition();
            _compactWindow?.CloseFromOwner();
            _trayIcon?.Dispose();
        };
        LocationChanged += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(SaveNormalWindowPosition));
        if (WindowPositionStore.TryLoad("main", MinWidth, MinHeight, out var position))
        {
            Left = position.X;
            Top = position.Y;
        }
    }

    private void SaveNormalWindowPosition()
    {
        if (WindowPositionStore.CanPersist(WindowState, Left, Top))
        {
            WindowPositionStore.Save("main", Left, Top);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var captionColor = ((SolidColorBrush)FindResource("WindowBrush")).Color;
        var borderColor = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        var textColor = ((SolidColorBrush)FindResource("TextPrimaryBrush")).Color;
        var caption = ToColorRef(captionColor.R, captionColor.G, captionColor.B);
        var border = ToColorRef(borderColor.R, borderColor.G, borderColor.B);
        var text = ToColorRef(textColor.R, textColor.G, textColor.B);
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(MainWindowMessageHook);
        UpdateIntegratedTitleBarLayout();
        _trayIcon = new TrayIconController(
            this,
            ActivateFromExistingInstance,
            ShowCompactView,
            () => Application.Current.Shutdown());
        HideToTrayButton.IsEnabled = _trayIcon.IsAvailable;
        if (!_trayIcon.IsAvailable)
        {
            HideToTrayButton.ToolTip = "Tray icon is unavailable";
        }
    }

    private void HeaderBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateIntegratedTitleBarLayout();

    private void UpdateIntegratedTitleBarLayout()
    {
        if (!IsLoaded || HeaderBar.ActualWidth <= 0)
        {
            return;
        }

        var captionControlsWidth = 3 * SystemParameters.WindowCaptionButtonWidth;
        var captionButtonWidth = SystemParameters.WindowCaptionButtonWidth;
        SystemCaptionButtons.Width = captionControlsWidth;
        MinimizeButton.Width = captionButtonWidth;
        MaximizeRestoreButton.Width = captionButtonWidth;
        CloseButton.Width = captionButtonWidth;
        HeaderLayout.Margin = new Thickness(22, 0, captionControlsWidth, 0);
    }

    private IntPtr MainWindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && ApplyMaximizedWorkArea(hwnd, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateIntegratedTitleBarLayout));
        }

        return IntPtr.Zero;
    }

    internal static MaximizedWorkAreaBounds CalculateMaximizedWorkAreaBounds(
        int monitorLeft,
        int monitorTop,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom) =>
        new(
            X: workLeft - monitorLeft,
            Y: workTop - monitorTop,
            Width: workRight - workLeft,
            Height: workBottom - workTop);

    private static bool ApplyMaximizedWorkArea(IntPtr hwnd, IntPtr minMaxInfoPointer)
    {
        if (minMaxInfoPointer == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var bounds = CalculateMaximizedWorkAreaBounds(
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right,
            monitorInfo.Work.Bottom);
        var minMaxInfo = Marshal.PtrToStructure<NativeMinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition = new NativePoint(bounds.X, bounds.Y);
        minMaxInfo.MaxSize = new NativePoint(bounds.Width, bounds.Height);
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, fDeleteOld: false);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y) => (X, Y) = (x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static int ToColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AttachCardHover(FiveHourCard);
        AttachCardHover(WeeklyCard);
        AttachCardHover(ReserveCard);
        AttachCardHover(TodayCard);
        AttachCardHover(QuotaHistoryCard);
        AttachCardHover(DailyHistoryCard);
        await RefreshUsageAsync();
        await RefreshAlertEventsAsync();
        if (IsLoaded)
        {
            if (!_successfulLaunchRecorded)
            {
                _successfulLaunchRecorded = true;
                _supportEngagement.RecordSuccessfulLaunch(DateTimeOffset.UtcNow);
            }
            _telemetryService?.Start();
            _refreshTimer.Start();
            _notificationRefreshTimer.Start();
        }
    }

    private async void NotificationRefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshAlertEventsAsync();

    private async Task RefreshAlertEventsAsync()
    {
        try
        {
            var hasBaseline = _notificationBaselineInitialized;
            var lastSeenId = _lastSeenAlertEventId;
            var result = await Task.Run(() =>
            {
                var events = _usageReader.ReadRecentAlertEvents();
                var newEvents = hasBaseline
                    ? _usageReader.ReadAlertEventsAfter(lastSeenId)
                    : Array.Empty<AlertEventSnapshot>();
                var deliveryMode = _usageReader.ReadNotificationDeliveryMode();
                var badgeMode = _usageReader.ReadNotificationBadgeMode();
                var soundMode = _usageReader.ReadNotificationSoundMode();
                return (Events: events, NewEvents: newEvents,
                    DeliveryMode: deliveryMode, BadgeMode: badgeMode, SoundMode: soundMode);
            });
            _alertEvents = result.Events
                .Select(alert => alert with { Accent = new SolidColorBrush(alert.AccentColor) })
                .ToArray();
            _notificationDeliveryMode = result.DeliveryMode;
            _notificationBadgeMode = result.BadgeMode;
            _notificationSoundMode = result.SoundMode;
            if (!_notificationBaselineInitialized)
            {
                _lastSeenAlertEventId = _alertEvents.Count == 0 ? 0 : _alertEvents.Max(alert => alert.Id);
                _notificationBaselineInitialized = true;
            }
            else
            {
                foreach (var newEvent in result.NewEvents)
                {
                    _lastSeenAlertEventId = Math.Max(_lastSeenAlertEventId, newEvent.Id);
                }

                if (_notificationDeliveryMode != "off")
                {
                    foreach (var group in result.NewEvents.GroupBy(alert =>
                                 (alert.Kind, alert.EventType, alert.Message)))
                    {
                        var latest = group.MaxBy(alert => alert.Id)!;
                        _notificationService?.ShowAlert(
                            latest, suppressDisplay: _notificationDeliveryMode == "quiet");
                    }
                }

                var shouldPlaySound = _notificationSoundMode == "all"
                    ? result.NewEvents.Count > 0
                    : _notificationSoundMode == "important"
                        && result.NewEvents.Any(alert => alert.EventType is "LOW_THRESHOLD" or "SOURCE_STALE");
                if (shouldPlaySound)
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
            }
            UpdateNotificationBadge();
            UpdateNotificationSettingsLabel();
            UpdateNotificationPlatformStatus();
            if (NotificationPopup.IsOpen)
            {
                var feed = GroupAlertEvents(_alertEvents, _notificationEventToFocus);
                NotificationItems.ItemsSource = feed;
                UpdateNotificationSummary(feed);
                NotificationEmptyText.Visibility = _alertEvents.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or SqliteException
                                   or IOException or UnauthorizedAccessException)
        {
            _alertEvents = [];
            UpdateNotificationBadge();
            NotificationPlatformStatusText.Text = _notificationService?.Status
                ?? "Windows notifications are unavailable";
        }
    }

    private void UpdateNotificationPlatformStatus() =>
        NotificationPlatformStatusText.Text = _notificationService?.Status
            ?? "Windows notifications are unavailable";

    private void UpdateNotificationBadge()
    {
        var unread = GroupAlertEvents(_alertEvents).Count(item => item.IsUnread);
        var showBadge = unread > 0 && _notificationBadgeMode != "off";
        var showCount = showBadge && _notificationBadgeMode == "count";
        NotificationBadge.Visibility = showBadge ? Visibility.Visible : Visibility.Collapsed;
        NotificationBadgeText.Text = showCount
            ? unread > 9 ? "9+" : unread.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        NotificationBadge.Width = showCount ? double.NaN : 8;
        NotificationBadge.MinWidth = showCount ? 15 : 8;
        NotificationBadge.Height = showCount ? 14 : 8;
        NotificationBadge.CornerRadius = showCount ? new CornerRadius(7) : new CornerRadius(4);
        NotificationBadge.Background = (Brush)FindResource(showCount ? "InteractionBrush" : "UsageBrush");
        AutomationProperties.SetName(NotificationButton, unread == 0
            ? "Activity. No unread groups."
            : $"Activity. {unread} unread group{(unread == 1 ? string.Empty : "s")}.");
    }

    private void UpdateNotificationSettingsLabel() =>
        SetNotificationSettingsPresentation();

    private void SetNotificationSettingsPresentation()
    {
        NotificationDeliveryButton.Content = _notificationDeliveryMode switch
        {
            "off" => "Windows: Off",
            "banner" => "Windows: Banner",
            _ => "Windows: Quiet"
        };
        NotificationDeliveryButton.ToolTip = CreateNotificationModeToolTip(
            "Banner delivery",
            _notificationDeliveryMode,
            ("quiet", "Quiet", "No pop-up; Windows keeps it in Notification Center."),
            ("banner", "Banner", "Show a Windows pop-up for each new alert group."),
            ("off", "Off", "Stop Windows alerts; the in-app Activity feed stays available."));
        NotificationBadgeModeButton.Content = _notificationBadgeMode switch
        {
            "off" => "Badge: Off",
            "count" => "Badge: Count",
            _ => "Badge: Dot"
        };
        NotificationBadgeModeButton.ToolTip = CreateNotificationModeToolTip(
            "Unread badge",
            _notificationBadgeMode,
            ("dot", "Dot", "Show a small dot while unread activity is available."),
            ("count", "Count", "Show the number of unread groups on the Activity button."),
            ("off", "Off", "Hide the badge; activity and alerts remain available."));
        NotificationSoundModeButton.Content = _notificationSoundMode switch
        {
            "all" => "Sound: All",
            "important" => "Sound: Important",
            _ => "Sound: Off"
        };
        NotificationSoundModeButton.ToolTip = CreateNotificationModeToolTip(
            "Alert sound",
            _notificationSoundMode,
            ("important", "Important", "Sound only for low quota and stale source alerts."),
            ("all", "All alerts", "Play a sound for every new alert group."),
            ("off", "Off", "Keep alert sounds silent."));
        foreach (var button in new[]
                 { NotificationDeliveryButton, NotificationBadgeModeButton, NotificationSoundModeButton })
        {
            ToolTipService.SetInitialShowDelay(button, 350);
            ToolTipService.SetShowDuration(button, 15000);
        }
        TestNotificationButton.IsEnabled = _notificationService?.AreEnabled ?? false;
        TestNotificationSoundButton.IsEnabled = true;
        NotificationPlatformStatusText.Text = _notificationService?.Status
            ?? "Windows notifications are unavailable";
    }

    private StackPanel CreateNotificationModeToolTip(
        string title,
        string currentMode,
        params (string Key, string Name, string Description)[] modes)
    {
        var content = new StackPanel { MaxWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = $"{title} · click to cycle",
            FontFamily = (FontFamily)FindResource("UiFontFamily"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 5)
        });

        foreach (var mode in modes)
        {
            var isCurrent = mode.Key == currentMode;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = isCurrent ? "●" : "○",
                FontFamily = (FontFamily)FindResource("UiFontFamily"),
                FontSize = 10,
                Foreground = (Brush)FindResource(isCurrent ? "InteractionBrush" : "TextMutedBrush"),
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = mode.Name,
                        FontFamily = (FontFamily)FindResource("UiFontFamily"),
                        FontSize = 10,
                        FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = (Brush)FindResource("TextPrimaryBrush")
                    },
                    new TextBlock
                    {
                        Text = mode.Description,
                        FontFamily = (FontFamily)FindResource("UiFontFamily"),
                        FontSize = 9,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)FindResource("TextSecondaryBrush")
                    }
                }
            });
            content.Children.Add(new Border
            {
                Background = isCurrent ? (Brush)FindResource("SurfaceBrush") : Brushes.Transparent,
                BorderBrush = isCurrent ? (Brush)FindResource("InteractionBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(isCurrent ? 1 : 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 4, 5, 4),
                Margin = new Thickness(0, 0, 0, 3),
                Child = row
            });
        }

        return content;
    }

    private async void NotificationButton_Click(object sender, RoutedEventArgs e) =>
        await OpenNotificationCenterAsync();

    private async Task OpenNotificationCenterAsync()
    {
        await RefreshAlertEventsAsync();
        var focusedAlertId = _notificationEventToFocus;
        var feed = GroupAlertEvents(_alertEvents, focusedAlertId);
        NotificationItems.ItemsSource = feed;
        NotificationEmptyText.Visibility = _alertEvents.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateNotificationSummary(feed);
        _notificationEventToFocus = null;
        NotificationPopup.IsOpen = true;

        if (focusedAlertId is long alertId)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var focusedIndex = feed.ToList().FindIndex(item => item.AlertIds.Contains(alertId));
                if (focusedIndex >= 0
                    && NotificationItems.ItemContainerGenerator.ContainerFromIndex(focusedIndex)
                        is FrameworkElement container)
                {
                    container.BringIntoView();
                }
            });
        }
    }

    public void ActivateFromExistingInstance()
    {
        RestoreAndActivate();
    }

    public void ActivateFromMini()
    {
        RestoreAndActivate(forceNormal: true);
    }

    public void ActivateFromNotification(long? alertId)
    {
        _notificationEventToFocus = alertId;
        RestoreAndActivate();
        _ = OpenNotificationCenterAsync();
    }

    public void ActivateFromTestNotification() => RestoreAndActivate();

    private void RestoreAndActivate(bool forceNormal = false)
    {
        _compactWindow?.HideAndSave();
        if (!IsVisible)
        {
            Show();
        }
        if (forceNormal || WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _ = SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    private void CompactView_Click(object sender, RoutedEventArgs e) => ShowCompactView();

    private void UpdateMaximizeRestoreGlyph()
    {
        if (MaximizeRestoreGlyph is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreGlyph.Text = isMaximized ? "\uE923" : "\uE922";
        MaximizeRestoreButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeRestoreButton, isMaximized ? "Restore" : "Maximize");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ResetCompactPosition_Click(object sender, RoutedEventArgs e)
    {
        _compactWindow ??= new CompactWindow(this);
        _compactWindow.ResetPositionToPrimaryDisplay();
        if (!_compactWindow.IsVisible)
        {
            _compactWindow.Show();
        }
        _compactWindow.Activate();
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Background_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || IsInsideNonDraggableRegion(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
        e.Handled = true;
    }

    private static bool IsInsideNonDraggableRegion(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Selector or TextBoxBase or ScrollBar or Thumb or Slider)
            {
                return true;
            }

            if (element is FrameworkElement frameworkElement
                && NonDraggableRegions.Contains(frameworkElement.Name))
            {
                return true;
            }

            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void ShowCompactView()
    {
        _compactWindow ??= new CompactWindow(this);
        _compactWindow.UpdateValues(_snapshot, _quotaSourceUnavailable);
        if (!_compactWindow.IsVisible)
        {
            _compactWindow.Show();
        }
        _compactWindow.Activate();
        Hide();
    }

    private void NotificationPopup_CloseClick(object sender, RoutedEventArgs e) =>
        NotificationPopup.IsOpen = false;

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => _usageReader.MarkRecentAlertEventsRead());
            _alertEvents = _alertEvents.Select(alert => alert with { IsUnread = false }).ToArray();
            var feed = GroupAlertEvents(_alertEvents);
            NotificationItems.ItemsSource = feed;
            UpdateNotificationSummary(feed);
            UpdateNotificationBadge();
        }
        catch (Exception ex) when (ex is FileNotFoundException or SqliteException
                                   or IOException or UnauthorizedAccessException)
        {
            SetRefreshStatus("Could not save notification read state.", animate: false);
        }
    }

    private void NotificationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NotificationFeedItem item })
        {
            return;
        }

        NotificationPopup.IsOpen = false;
        FrameworkElement target = item.Kind switch
        {
            "5-hour" => FiveHourCard,
            "weekly" => WeeklyCard,
            "reserve" => ReserveCard,
            _ => TodayCard
        };
        target.BringIntoView();
    }

    private void NotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        var expanded = NotificationSettingsPanel.Visibility != Visibility.Visible;
        NotificationSettingsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        NotificationSettingsButton.Content = expanded ? "Hide settings" : "Settings";
        if (expanded)
        {
            UpdateNotificationPlatformStatus();
        }
    }

    internal static IReadOnlyList<NotificationFeedItem> GroupAlertEvents(
        IReadOnlyList<AlertEventSnapshot> events,
        long? focusedAlertId = null) => events
        .GroupBy(alert => (alert.Kind, alert.EventType))
        .Select(group =>
        {
            var latest = group.MaxBy(alert => alert.EventAt)!;
            var alertsById = group.OrderBy(alert => alert.Id).ToArray();
            var alertIds = alertsById.Select(alert => alert.Id).ToArray();
            var category = latest.EventType switch
            {
                "LOW_THRESHOLD" => "LOW QUOTA",
                "RESET" => "QUOTA RESET",
                "RECOVERY" => "RECOVERING",
                "SOURCE_STALE" => "DATA STALE",
                "SOURCE_RESTORED" => "DATA RESTORED",
                _ => "UPDATE"
            };
            return new NotificationFeedItem(
                latest.Id,
                alertIds,
                latest.Kind,
                latest.Message,
                $"{latest.Kind.ToUpperInvariant()} · {category}",
                latest.EventAt,
                latest.Accent,
                group.Any(alert => alert.IsUnread),
                group.Count(),
                focusedAlertId is long id && alertIds.Contains(id));
        })
        .OrderByDescending(item => item.EventAt)
        .ToArray();

    private void UpdateNotificationSummary(IReadOnlyList<NotificationFeedItem> feed)
    {
        var unread = feed.Count(item => item.IsUnread);
        NotificationSummaryText.Text = feed.Count == 0
            ? "Recent quota and data updates"
            : $"{feed.Count} groups · {_alertEvents.Count} events · {unread} unread";
        MarkAllReadButton.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static CustomPopupPlacement[] PlaceNotificationPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        var rightAligned = targetSize.Width - popupSize.Width;
        return
        [
            new CustomPopupPlacement(
                new Point(rightAligned, targetSize.Height + 6), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(
                new Point(rightAligned, -popupSize.Height - 6), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(
                new Point(0, targetSize.Height + 6), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(
                new Point(0, -popupSize.Height - 6), PopupPrimaryAxis.Vertical)
        ];
    }

    private async void NotificationDelivery_Click(object sender, RoutedEventArgs e)
    {
        var mode = NextMode(_notificationDeliveryMode, "quiet", "banner", "off");
        await SaveNotificationPreferenceAsync(
            () => Task.Run(() => _usageReader.SaveNotificationDeliveryMode(mode)),
            () => _notificationDeliveryMode = mode);
    }

    private async void NotificationBadgeMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = NextMode(_notificationBadgeMode, "dot", "count", "off");
        await SaveNotificationPreferenceAsync(
            () => Task.Run(() => _usageReader.SaveNotificationBadgeMode(mode)),
            () => _notificationBadgeMode = mode);
    }

    private async void NotificationSoundMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = NextMode(_notificationSoundMode, "important", "all", "off");
        await SaveNotificationPreferenceAsync(
            () => Task.Run(() => _usageReader.SaveNotificationSoundMode(mode)),
            () => _notificationSoundMode = mode);
    }

    private static string NextMode(string current, params string[] modes)
    {
        var index = Array.IndexOf(modes, current);
        return modes[(index + 1) % modes.Length];
    }

    private async Task SaveNotificationPreferenceAsync(Func<Task> save, Action apply)
    {
        try
        {
            await save();
            apply();
            UpdateNotificationSettingsLabel();
            UpdateNotificationBadge();
            if (NotificationPopup.IsOpen)
            {
                UpdateNotificationSummary(GroupAlertEvents(_alertEvents));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or SqliteException
                                   or IOException or UnauthorizedAccessException)
        {
            SetRefreshStatus("Could not save notification preference.", animate: false);
        }
    }

    private void OpenWindowsNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetRefreshStatus("Windows notification settings could not be opened.", animate: false);
        }
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        if (_notificationService?.ShowTestNotification() == true)
        {
            NotificationPlatformStatusText.Text = "Test notification sent";
        }
        else
        {
            UpdateNotificationPlatformStatus();
        }
    }

    private void TestNotificationSound_Click(object sender, RoutedEventArgs e) =>
        System.Media.SystemSounds.Asterisk.Play();

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        var includeHistory = _lastHistoryReadAt is null
            || DateTimeOffset.UtcNow - _lastHistoryReadAt >= TimeSpan.FromSeconds(30);
        await RefreshUsageAsync(includeHistory);
        TryShowSupportPrompt();
    }

    private void SupportDevelopment_Click(object sender, RoutedEventArgs e)
    {
        _supportEngagement.RecordSupportLinkOpened();
        SupportPromptPopup.Visibility = Visibility.Collapsed;
        OpenSupportPage();
    }

    private void SupportPromptClose_Click(object sender, RoutedEventArgs e) =>
        SupportPromptPopup.Visibility = Visibility.Collapsed;

    private void SupportPromptCta_Click(object sender, RoutedEventArgs e)
    {
        _supportEngagement.RecordSupportLinkOpened();
        SupportPromptPopup.Visibility = Visibility.Collapsed;
        OpenSupportPage();
    }

    private void OpenSupportPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SupportHubUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetRefreshStatus("Could not open the QuotaArc support page.", animate: false);
        }
    }

    private void TryShowSupportPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        if (IsActive && IsVisible && WindowState != WindowState.Minimized)
        {
            _supportActiveSince ??= now;
        }
        else
        {
            _supportActiveSince = null;
        }

        var conditions = new SupportPromptConditions(
            HealthyLiveState: IsHealthyLiveQuotaState(_snapshot, now) && !_quotaSourceUnavailable,
            MainWindowActive: IsActive && IsVisible && WindowState != WindowState.Minimized,
            SessionReady: _supportActiveSince is { } activeSince && now - activeSince >= SupportPromptActiveDelay,
            SessionStartedAfterMinimumAge: _supportActiveSince is { } sessionStart
                && _supportEngagement.State.FirstSuccessfulLaunchUtc is { } firstLaunch
                && sessionStart >= firstLaunch.AddHours(24),
            HistoryReady: _lastHistoryReadAt is not null,
            ConflictingSurfaceOpen: NotificationPopup.IsOpen
                || SupportPromptPopup.Visibility == Visibility.Visible
                || (Application.Current?.Windows.OfType<Window>()
                    .Any(window => window != this && window.IsVisible) ?? false));

        if (SupportPromptEligibility.IsEligible(_supportEngagement.State, now, conditions))
        {
            ShowSupportPrompt();
        }
    }

    private static bool IsHealthyLiveQuotaState(DashboardUsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return false;
        var requiredQuotas = new[] { snapshot.FiveHour, snapshot.Weekly };
        var availableQuotas = requiredQuotas.Append(snapshot.Reserve)
            .Where(quota => quota is not null).Select(quota => quota!).ToArray();
        if (!requiredQuotas.All(quota => quota is not null
                && quota.Source == LiveQuotaSource
                && QuotaFreshness.GetState(quota, now) == QuotaSampleState.Current)
            || !availableQuotas.All(quota => quota.Source == LiveQuotaSource
                && QuotaFreshness.GetState(quota, now) == QuotaSampleState.Current))
        {
            return false;
        }

        var latestAuthoritativeSample = availableQuotas.MinBy(quota => quota.SampledAt);
        var updateStatus = QuotaFreshness.GetUpdateStatus(
            latestAuthoritativeSample, now, allQuotasFromLiveSource: true, liveSourceAvailable: true);
        return updateStatus.State == QuotaSampleState.Current
            && updateStatus.Text.StartsWith("Live ·", StringComparison.Ordinal);
    }

    private void ShowSupportPrompt()
    {
        if (SupportPromptPopup.Visibility == Visibility.Visible)
        {
            return;
        }

        _supportEngagement.RecordPromptShown();
        SupportPromptPopup.Visibility = Visibility.Visible;
        SupportPromptPopup.Opacity = 0;
        SupportPromptTranslate.Y = 6;
        if (!SystemParameters.ClientAreaAnimation)
        {
            SupportPromptPopup.Opacity = 1;
            SupportPromptTranslate.Y = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(150);
        SupportPromptPopup.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = (IEasingFunction)FindResource("MotionEaseOut") });
        SupportPromptTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, duration) { EasingFunction = (IEasingFunction)FindResource("MotionEaseOut") });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        AnimateRefreshIcon();
        _manualRefreshInProgress = true;
        RefreshButton.IsEnabled = false;
        SetRefreshStatus("Checking Codex sources…", animate: true);
        try
        {
            if (_telemetryService is not null)
            {
                var result = await _telemetryService.RefreshNowAsync();
                _sourceRefreshStatus = result.Status;
                _quotaSourceUnavailable = !result.AppServerAvailable;
            }
            await RefreshUsageAsync();
            await RefreshAlertEventsAsync();
        }
        finally
        {
            _manualRefreshInProgress = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void TelemetryService_Refreshed(DataRefreshResult result)
    {
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            _sourceRefreshStatus = result.Status;
            _quotaSourceUnavailable = !result.AppServerAvailable;
            await RefreshUsageAsync();
            await RefreshAlertEventsAsync();
        }));
    }

    private void AttachCardHover(Border card)
    {
        card.MouseEnter += Card_MouseEnter;
        card.MouseLeave += Card_MouseLeave;
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        var highlight = ((SolidColorBrush)FindResource("InteractionBrush")).Color;
        AnimateBrush(card, Border.BorderBrushProperty, highlight, animate: true);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        var border = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        AnimateBrush(card, Border.BorderBrushProperty, border, animate: true);
    }

    private void AnimateBrush(DependencyObject target, DependencyProperty property, Color color, bool animate)
    {
        var current = target.GetValue(property) as SolidColorBrush;
        if (current?.Color == color)
        {
            return;
        }

        var from = current?.Color ?? color;
        var brush = new SolidColorBrush(from);
        target.SetValue(property, brush);
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            brush.Color = color;
            return;
        }

        brush.Color = color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(from, color, (Duration)FindResource("RangeMotionDuration"))
            {
                EasingFunction = (IEasingFunction)FindResource("MotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void SetValueText(TextBlock textBlock, string value, bool animate = true)
    {
        if (textBlock.Text == value)
        {
            return;
        }

        textBlock.BeginAnimation(UIElement.OpacityProperty, null);
        textBlock.Text = value;
        textBlock.Opacity = 1;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            textBlock.RenderTransform = Transform.Identity;
            return;
        }

        var duration = (Duration)FindResource("ValueMotionDuration");
        textBlock.RenderTransform = Transform.Identity;
        textBlock.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = (IEasingFunction)FindResource("MotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void TodayValueText_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_snapshot is not null)
        {
            UpdateTodayValueLabel(_snapshot.Today.Total, animate: false);
        }
    }

    private void UpdateTodayValueLabel(long total, bool animate)
    {
        var fullValue = $"{Format(total)} tokens";
        var width = TodayValueText.ActualWidth;
        var pixelsPerDip = VisualTreeHelper.GetDpi(TodayValueText).PixelsPerDip;
        var fittedValue = FitTodayValueLabel(total, width, candidate =>
        {
            var formatted = new FormattedText(
                candidate,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(TodayValueText.FontFamily, TodayValueText.FontStyle,
                    TodayValueText.FontWeight, TodayValueText.FontStretch),
                TodayValueText.FontSize,
                Brushes.Black,
                pixelsPerDip);
            return formatted.Width;
        });

        TodayValueText.ToolTip = fullValue;
        AutomationProperties.SetHelpText(TodayValueText, fullValue);
        SetValueText(TodayValueText, fittedValue, animate);
    }

    internal static string FitTodayValueLabel(long value, double availableWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (availableWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        }

        var candidates = new[]
        {
            $"{Format(value)} tokens",
            $"{FormatCompact(value)} tokens",
            $"{FormatCompactInteger(value)} tokens",
            FormatCompactInteger(value)
        };
        return candidates.FirstOrDefault(candidate => measure(candidate) + 4 <= availableWidth)
            ?? candidates[^1];
    }

    private void SetProgressValue(ProgressBar progressBar, double value)
    {
        var from = progressBar.Value;
        if (Math.Abs(from - value) < 0.01)
        {
            return;
        }

        progressBar.BeginAnimation(RangeBase.ValueProperty, null);
        progressBar.Value = value;
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        progressBar.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(from, value, (Duration)FindResource("ProgressMotionDuration"))
            {
                EasingFunction = (IEasingFunction)FindResource("MotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void AnimateRefreshIcon()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var rotate = RefreshIcon.RenderTransform as RotateTransform;
        if (rotate is null)
        {
            rotate = new RotateTransform();
            RefreshIcon.RenderTransform = rotate;
        }
        else if (rotate.IsFrozen)
        {
            rotate = rotate.Clone();
            RefreshIcon.RenderTransform = rotate;
        }

        rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        rotate.Angle = 0;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, (Duration)FindResource("ChartMotionDuration"))
            {
                EasingFunction = (IEasingFunction)FindResource("MotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void SetRefreshStatus(string value, bool animate)
    {
        if (RefreshStatusText.Text == value)
        {
            return;
        }

        RefreshStatusText.BeginAnimation(UIElement.OpacityProperty, null);
        RefreshStatusText.Text = value;
        RefreshStatusText.Opacity = 1;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        RefreshStatusText.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, (Duration)FindResource("ValueMotionDuration"))
            {
                EasingFunction = (IEasingFunction)FindResource("MotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private async void HistoryRange_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRangeSaveInProgress || sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var range))
        {
            return;
        }

        string preferenceKey;
        if (parts[0] == "daily" && range is 7 or 14 or 30 or 90)
        {
            _historyDays = range;
            preferenceKey = "history_daily_range";
        }
        else if (parts[0] == "quota" && range is 6 or 12 or 24 or 168 or 336 or 720)
        {
            _quotaHours = range;
            _quotaChartNeedsRender = true;
            preferenceKey = "history_quota_range";
        }
        else
        {
            return;
        }

        if (parts[0] == "daily")
        {
            _dailyChartNeedsRender = true;
        }

        UpdateRangeButtonStyles(animate: true);
        _historyRangeSaveInProgress = true;
        var preferenceSaveFailed = false;
        try
        {
            await Task.Run(() => _usageReader.SaveHistoryRangePreference(preferenceKey, range));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            preferenceSaveFailed = true;
        }

        try
        {
            await RefreshUsageAsync();
            if (preferenceSaveFailed)
            {
                SetRefreshStatus("Chart range changed for this session, but its preference could not be saved.", animate: false);
            }
        }
        finally
        {
            _historyRangeSaveInProgress = false;
        }
    }

    private async Task RefreshUsageAsync(bool includeHistory = true)
    {
        if (_refreshInProgress)
        {
            _historyRefreshRequested |= includeHistory;
            return;
        }

        _refreshInProgress = true;
        if (includeHistory || _manualRefreshInProgress)
        {
            RefreshButton.IsEnabled = false;
        }
        if (_manualRefreshInProgress)
        {
            SetRefreshStatus("Reading local usage and quota history read-only…", animate: true);
        }

        try
        {
            var localDay = DateOnly.FromDateTime(DateTime.Now);
            int? requestedHistory = _rangePreferencesLoaded ? _historyDays : null;
            int? requestedQuota = _rangePreferencesLoaded ? _quotaHours : null;
            var snapshot = await Task.Run(() => _usageReader.ReadDashboard(
                localDay,
                requestedHistory,
                requestedQuota,
                includeHistory: includeHistory));
            ShowDashboard(snapshot, includeHistory);
        }
        catch (FileNotFoundException)
        {
            ShowUnavailable("Local usage database not found. No database was created.");
        }
        catch (Exception)
        {
            ShowUnavailable("Could not read local usage data. Check the database and refresh.");
        }
        finally
        {
            _refreshInProgress = false;
            RefreshButton.IsEnabled = true;
            if (_historyRefreshRequested)
            {
                _historyRefreshRequested = false;
                await RefreshUsageAsync();
            }
        }
    }

    private void ShowDashboard(DashboardUsageSnapshot snapshot, bool includeHistory)
    {
        QuotaArcProfileMigrator.RetireLegacyProfileAfterSuccessfulRead();
        var currentDayChanged = _snapshot?.Today.LocalDay != snapshot.Today.LocalDay;
        if (_snapshot is not null && snapshot.History.Count == 0)
        {
            snapshot = snapshot with
            {
                History = _snapshot.History,
                QuotaHistory = _snapshot.QuotaHistory,
                QuotaResetEvents = _snapshot.QuotaResetEvents
            };
        }

        snapshot = snapshot with
        {
            QuotaResetEvents = ReconcileQuotaResetEvents(
                NormalizeQuotaHistory(snapshot.QuotaHistory), snapshot.QuotaResetEvents)
        };

        var renderQuotaChart = _quotaChartNeedsRender
            || _snapshot is null
            || !_snapshot.QuotaHistory.SequenceEqual(snapshot.QuotaHistory)
            || !_snapshot.QuotaResetEvents.SequenceEqual(snapshot.QuotaResetEvents);
        var renderDailyChart = _dailyChartNeedsRender
            || _snapshot is null
            || !_snapshot.History.SequenceEqual(snapshot.History);
        _snapshot = snapshot;
        if (!_quotaSourceUnavailable && IsHealthyLiveQuotaState(snapshot, snapshot.ReadAt))
        {
            _supportEngagement.RecordLiveQuotaData();
        }
        _compactWindow?.UpdateValues(snapshot, _quotaSourceUnavailable);
        if (includeHistory)
        {
            _lastHistoryReadAt = snapshot.ReadAt;
        }
        if (!_rangePreferencesLoaded)
        {
            _historyDays = snapshot.DailyHistoryDays;
            _quotaHours = snapshot.QuotaHistoryHours;
            _rangePreferencesLoaded = true;
            UpdateRangeButtonStyles();
        }

        var today = snapshot.Today;
        _history = snapshot.History;
        _visibleQuotaResetEvents = snapshot.QuotaResetEvents;
        if (_selectedHistoryDay is null || currentDayChanged)
        {
            _selectedHistoryDay = today.LocalDay;
        }
        UpdateTodayValueLabel(today.Total, animate: true);
        UpdateTodaySummary(today);
        RenderTodaySparkline();
        SetQuotaCard(
            snapshot.FiveHour,
            FiveHourValueText,
            FiveHourProgress,
            FiveHourDetailText,
            snapshot,
            forecastHours: 6,
            isWeekly: false);
        SetQuotaCard(
            snapshot.Weekly,
            WeeklyValueText,
            WeeklyProgress,
            WeeklyDetailText,
            snapshot,
            forecastHours: 168,
            isWeekly: true);
        SetQuotaCard(
            snapshot.Reserve,
            ReserveValueText,
            ReserveProgress,
            ReserveDetailText,
            snapshot,
            forecastHours: 168,
            isWeekly: true,
            showForecast: false);
        if (renderQuotaChart)
        {
            RenderQuotaChart();
            _quotaChartNeedsRender = false;
        }
        if (renderDailyChart)
        {
            RenderDailyHistory();
            _dailyChartNeedsRender = false;
        }
        SetRefreshStatus($"SQLite {today.SqliteVersion} · {_sourceRefreshStatus}", _manualRefreshInProgress);
        UpdateUpdatedStatus(snapshot);
    }

    private void UpdateUpdatedStatus(DashboardUsageSnapshot snapshot)
    {
        var quotas = new[] { snapshot.FiveHour, snapshot.Weekly, snapshot.Reserve }
            .Where(quota => quota is not null)
            .Select(quota => quota!)
            .ToArray();
        // Use the oldest available quota sample so the single header status
        // never implies that every quota is fresh when one of them is stale.
        var sample = quotas.MinBy(quota => quota.SampledAt);
        var allQuotasFromLiveSource = quotas.All(quota => quota.Source == "APP_SERVER_LIVE");
        ApplyUpdatedStatus(sample, quotas, snapshot.ReadAt, allQuotasFromLiveSource);
    }

    private void ApplyUpdatedStatus(
        QuotaUsageSnapshot? sample,
        IReadOnlyList<QuotaUsageSnapshot> quotas,
        DateTimeOffset now,
        bool allQuotasFromLiveSource)
    {
        var status = QuotaFreshness.GetUpdateStatus(
            sample, now, allQuotasFromLiveSource, liveSourceAvailable: !_quotaSourceUnavailable);
        if (UpdatedAtText.Text != status.Text)
        {
            UpdatedAtText.Text = status.Text;
        }

        UpdatedAtIndicator.Fill = status.State switch
        {
            QuotaSampleState.Current when status.Text.StartsWith("Live ·", StringComparison.Ordinal) =>
                (Brush)FindResource("UsageBrush"),
            QuotaSampleState.Current => (Brush)FindResource("TextMutedBrush"),
            QuotaSampleState.Unavailable => (Brush)FindResource("TextMutedBrush"),
            _ => (Brush)FindResource("FiveHourBrush")
        };

        var source = string.Join(", ", quotas.Select(quota => quota.Source).Distinct());
        UpdatedAtText.ToolTip = sample is null
            ? _quotaSourceUnavailable
                ? "The live quota source is offline and no quota sample is available."
                : "No quota sample is available from any source."
            : $"Quota sample at {sample.SampledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)} · {source}";
        UpdatedAtStatus.Visibility = Visibility.Visible;
    }

    private void UpdateTodaySummary(TodayUsageSnapshot today)
    {
        var recentDays = _history.TakeLast(7).ToArray();
        if (recentDays.Length > 0 && recentDays[^1].LocalDay == today.LocalDay)
        {
            recentDays[^1] = recentDays[^1] with { Total = today.Total };
        }
        var average = recentDays.Length == 0
            ? 0
            : recentDays.Average(day => (double)day.Total);
        var yesterday = recentDays.Length > 1 ? recentDays[^2].Total : 0;
        var ratio = average > 0 ? today.Total * 100.0 / average : (double?)null;
        var change = yesterday == 0
            ? "No usage yesterday"
            : $"{((today.Total - yesterday) * 100.0 / yesterday).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)}% vs yesterday";
        var averageText = ratio is null
            ? "n/a of 7-day avg"
            : $"{ratio.Value.ToString("0", CultureInfo.CurrentCulture)}% of 7-day avg";
        TodayDetailText.Text = $"{change} · {averageText}";
    }

    private void TodaySparkline_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTodaySparkline();

    private void RenderTodaySparkline()
    {
        TodaySparkline.Children.Clear();
        if (_snapshot is null || TodaySparkline.ActualWidth < 20 || TodaySparkline.ActualHeight < 8)
        {
            return;
        }

        var days = _history.TakeLast(7).ToArray();
        if (days.Length == 0)
        {
            return;
        }

        var today = _snapshot.Today;
        if (days[^1].LocalDay == today.LocalDay)
        {
            days[^1] = new DailyUsageSnapshot(
                today.LocalDay,
                today.SampleCount,
                today.Total,
                today.Input,
                today.Cached,
                today.Output,
                today.Reasoning);
        }

        var maxTotal = Math.Max(1, days.Max(day => day.Total));
        const double gap = 4;
        var barWidth = Math.Max(2, (TodaySparkline.ActualWidth - gap * (days.Length - 1)) / days.Length);
        var baseline = TodaySparkline.ActualHeight;
        for (var index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var barHeight = day.Total == 0
                ? 1
                : Math.Max(2, day.Total / (double)maxTotal * (baseline - 1));
            var isToday = day.LocalDay == today.LocalDay;
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = (Brush)FindResource(isToday ? "TodayBrush" : "UsageHistoryBrush"),
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = $"{day.LocalDay.ToString("dd MMM", CultureInfo.CurrentCulture)} · {Format(day.Total)} tokens",
                IsHitTestVisible = true
            };
            Canvas.SetLeft(bar, index * (barWidth + gap));
            Canvas.SetTop(bar, baseline - barHeight);
            TodaySparkline.Children.Add(bar);
        }
    }

    private void SetQuotaCard(
        QuotaUsageSnapshot? quota,
        TextBlock valueText,
        ProgressBar progressBar,
        TextBlock detailText,
        DashboardUsageSnapshot snapshot,
        int forecastHours,
        bool isWeekly,
        bool showForecast = true)
    {
        if (quota is null)
        {
            SetValueText(valueText, "NO DATA");
            valueText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            SetProgressValue(progressBar, 0);
            detailText.Text = "Quota data unavailable";
            return;
        }

        var remaining = 100.0 - quota.UsedPercent;
        var stale = QuotaFreshness.GetState(quota, snapshot.ReadAt) == QuotaSampleState.Stale;
        SetValueText(valueText, $"{remaining.ToString("0", CultureInfo.CurrentCulture)}% LEFT");
        var quotaBrush = quota.Kind switch
        {
            "5-hour" => (Brush)FindResource("FiveHourBrush"),
            "weekly" => (Brush)FindResource("WeeklyBrush"),
            _ => (Brush)FindResource("InteractionBrush")
        };
        valueText.Foreground = quotaBrush;
        SetProgressValue(progressBar, Math.Clamp(remaining, 0, 100));
        progressBar.Foreground = quotaBrush;

        var reset = ResetSummary(quota.ResetAt, snapshot.ReadAt);
        var forecast = Forecast(snapshot.QuotaHistory, quota.Kind, quota.ResetAt, snapshot.ReadAt, forecastHours, isWeekly);
        detailText.Inlines.Clear();
        detailText.Inlines.Add(new Run(reset));
        if (!stale && showForecast)
        {
            detailText.Inlines.Add(new LineBreak());
            detailText.Inlines.Add(new Run(forecast));
        }
    }

    private void UpdateRangeButtonStyles(bool animate = false)
    {
        foreach (var button in QuotaRangePanel.Children.OfType<Button>())
        {
            var selected = button.Tag is string tag
                && tag == $"quota:{_quotaHours}";
            SetRangeButtonStyle(button, selected, animate);
        }

        foreach (var button in DailyRangePanel.Children.OfType<Button>())
        {
            var selected = button.Tag is string tag
                && tag == $"daily:{_historyDays}";
            SetRangeButtonStyle(button, selected, animate);
        }
    }

    private void QuotaLegendToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string kind, IsChecked: bool isChecked })
        {
            return;
        }

        if (kind == "5-hour")
        {
            if (!isChecked && !_showWeeklyHistory)
            {
                FiveHourLegendToggle.IsChecked = true;
                return;
            }
            _showFiveHourHistory = isChecked;
        }
        else if (kind == "weekly")
        {
            if (!isChecked && !_showFiveHourHistory)
            {
                WeeklyLegendToggle.IsChecked = true;
                return;
            }
            _showWeeklyHistory = isChecked;
        }

        RenderQuotaChart();
    }

    private void SetRangeButtonStyle(Button button, bool selected, bool animate)
    {
        var background = ((SolidColorBrush)FindResource(selected ? "InteractionBrush" : "SurfaceBrush")).Color;
        var border = ((SolidColorBrush)FindResource(selected ? "InteractionBrush" : "BorderBrush")).Color;
        AnimateBrush(button, Control.BackgroundProperty, background, animate);
        AnimateBrush(button, Control.BorderBrushProperty, border, animate);
        button.Foreground = (Brush)FindResource(selected ? "WindowBrush" : "TextSecondaryBrush");
    }

    private void RenderQuotaChart()
    {
        QuotaChart.Children.Clear();
        _quotaCrosshair = null;
        _quotaFiveHourInspectionMarker = null;
        _quotaWeeklyInspectionMarker = null;
        _quotaInspectionTooltip = null;
        _quotaInspectionTooltipText = null;
        _visibleQuotaHistory = [];
        _visibleFiveHourHistory = [];
        _visibleWeeklyHistory = [];
        if (_snapshot is null || QuotaChart.ActualWidth < 80 || QuotaChart.ActualHeight < 80)
        {
            return;
        }

        var now = _snapshot.ReadAt;
        var start = now.AddHours(-_quotaHours);
        var width = QuotaChart.ActualWidth;
        var height = QuotaChart.ActualHeight;
        const double left = 42;
        const double right = 12;
        const double top = 27;
        const double bottom = 28;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var scale = new QuotaChartScale(start, now, left, top, plotWidth, plotHeight);
        var axisBrush = (Brush)FindResource("TextSecondaryBrush");
        var gridBrush = (Brush)FindResource("GridBrush");
        var plotLayer = new Canvas
        {
            Width = width,
            Height = height,
            Clip = new RectangleGeometry(new Rect(left, top, plotWidth, plotHeight))
        };
        QuotaChart.Children.Add(plotLayer);

        foreach (var percentage in new[] { 0, 25, 50, 75, 100 })
        {
            var y = scale.MapY(percentage);
            AddLine(plotLayer, left, y, width - right, y, gridBrush, 0.7);
            AddLabel(QuotaChart, percentage.ToString(CultureInfo.InvariantCulture), 1, y - 8, axisBrush, 10, 34);
        }

        var allVisible = NormalizeQuotaHistory(_snapshot.QuotaHistory)
            .Where(point => point.SampledAt >= start && point.SampledAt <= now)
            .ToArray();
        _visibleFiveHourHistory = _showFiveHourHistory
            ? allVisible.Where(point => point.Kind == "5-hour").ToArray()
            : [];
        _visibleWeeklyHistory = _showWeeklyHistory
            ? allVisible.Where(point => point.Kind == "weekly").ToArray()
            : [];
        _visibleQuotaHistory = _visibleFiveHourHistory.Concat(_visibleWeeklyHistory)
            .OrderBy(point => point.SampledAt).ToArray();
        var fiveHourBrush = (Brush)FindResource("FiveHourBrush");
        var weeklyBrush = (Brush)FindResource("WeeklyBrush");
        var longRange = _quotaHours >= 168;
        QuotaTitleText.Text = longRange
            ? "QUOTA TIMELINE · 5H USED / WEEKLY LEFT"
            : "QUOTA TIMELINE · REMAINING %";
        FiveHourLegendText.Text = longRange ? "5h used" : "5-hour";
        WeeklyLegendText.Text = longRange ? "Weekly left" : "Weekly";
        NoSampleLegendText.Visibility = longRange ? Visibility.Collapsed : Visibility.Visible;
        FiveHourLegendToggle.ToolTip = longRange
            ? "Show or hide observed 5-hour cycle usage bars"
            : "Show or hide 5-hour quota history";

        foreach (var kind in new[] { "5-hour", "weekly" })
        {
            var series = kind == "5-hour" ? _visibleFiveHourHistory : _visibleWeeklyHistory;
            if (series.Count == 0)
            {
                continue;
            }

            if (longRange && kind == "5-hour")
            {
                if (_showFiveHourHistory)
                {
                    var bars = BuildQuotaCycleBars(series);
                    var cellWidth = plotWidth * 5.0 / _quotaHours;
                    var barWidth = Math.Clamp(cellWidth * 0.68, 3.0, 12.0);
                    foreach (var bar in bars)
                    {
                        var x = scale.MapX(bar.CycleAt);
                        if (x < left - barWidth || x > width - right + barWidth)
                        {
                            continue;
                        }

                        var used = Math.Clamp(bar.UsedPercent, 0, 100);
                        var barTop = scale.MapY(used);
                        var barHeight = Math.Max(1, scale.DataBottom - barTop);
                        var cycleBar = new Border
                        {
                            Width = barWidth,
                            Height = barHeight,
                            Background = fiveHourBrush,
                            CornerRadius = new CornerRadius(Math.Min(2, barWidth / 3)),
                            Opacity = 0.78,
                            Tag = "quota-cycle-bar",
                            ToolTip = FormatQuotaCycleBarTooltip(bar, CultureInfo.CurrentCulture),
                            IsHitTestVisible = true
                        };
                        Canvas.SetLeft(cycleBar, scale.MapX(bar.CycleAt) - barWidth / 2);
                        Canvas.SetTop(cycleBar, barTop);
                        plotLayer.Children.Add(cycleBar);
                    }
                }
                continue;
            }

            Point ToChartPoint(QuotaHistoryPoint point) =>
                scale.Map(point.SampledAt, point.RemainingPercent);

            var segments = SplitQuotaHistoryAtGaps(series, MaximumQuotaGap);
            foreach (var segment in segments)
            {
                var segmentLimit = Math.Max(2, (int)Math.Ceiling(600.0 * segment.Count / series.Count));
                var points = Downsample(segment, segmentLimit);
                if (segment.Count > 1)
                {
                    points = points
                        .Concat(new[] { segment[0], segment[^1] })
                        .Distinct()
                        .OrderBy(point => point.SampledAt)
                        .ToArray();
                }

                if (points.Count == 1)
                {
                    var point = ToChartPoint(points[0]);
                    var isolatedPoint = new Ellipse
                    {
                        Width = 4,
                        Height = 4,
                        Fill = (Brush)FindResource(kind == "5-hour" ? "FiveHourBrush" : "WeeklyBrush"),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(isolatedPoint, point.X - 2);
                    Canvas.SetTop(isolatedPoint, point.Y - 2);
                    plotLayer.Children.Add(isolatedPoint);
                    continue;
                }

                var coordinates = points.Select(ToChartPoint).ToArray();
                var area = new Polygon
                {
                    Fill = (Brush)FindResource(kind == "5-hour" ? "FiveHourAreaBrush" : "WeeklyAreaBrush"),
                    IsHitTestVisible = false
                };
                foreach (var coordinate in coordinates)
                {
                    area.Points.Add(coordinate);
                }
                area.Points.Add(new Point(coordinates[^1].X, scale.DataBottom));
                area.Points.Add(new Point(coordinates[0].X, scale.DataBottom));
                plotLayer.Children.Add(area);

                var line = new Polyline
                {
                    Stroke = (Brush)FindResource(kind == "5-hour" ? "FiveHourBrush" : "WeeklyBrush"),
                    StrokeThickness = 1.8,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                foreach (var coordinate in coordinates)
                {
                    line.Points.Add(coordinate);
                }

                plotLayer.Children.Add(line);
            }

            foreach (var gap in longRange && kind == "5-hour"
                         ? Array.Empty<QuotaGapBridge>()
                         : FindQuotaGapBridges(series, MaximumQuotaGap))
            {
                var from = ToChartPoint(gap.From);
                var to = ToChartPoint(gap.To);
                var bridge = new Line
                {
                    X1 = from.X,
                    Y1 = from.Y,
                    X2 = to.X,
                    Y2 = to.Y,
                    Stroke = (Brush)FindResource(kind == "5-hour" ? "FiveHourBrush" : "WeeklyBrush"),
                    StrokeThickness = 1.53,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Opacity = 0.82,
                    ToolTip = $"No quota samples · dashed gap\n{gap.From.SampledAt.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture)} → {gap.To.SampledAt.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture)} · {FormatQuotaGap(gap.To.SampledAt - gap.From.SampledAt)}",
                    IsHitTestVisible = true
                };
                plotLayer.Children.Add(bridge);
            }

            var latestPoint = series[^1];
            var latest = scale.Map(latestPoint.SampledAt, latestPoint.RemainingPercent);
            var latestX = latest.X;
            var latestY = latest.Y;
            var seriesBrush = (Brush)FindResource(kind == "5-hour" ? "FiveHourBrush" : "WeeklyBrush");
            var staleTail = !longRange && now - latestPoint.SampledAt > MaximumQuotaGap;
            var tailLength = Math.Max(0, width - right - latestX);
            var visibleStaleTail = staleTail && ShouldRenderQuotaStaleTail(tailLength);
            if (visibleStaleTail)
            {
                plotLayer.Children.Add(new Line
                {
                    X1 = latestX,
                    Y1 = latestY,
                    X2 = width - right,
                    Y2 = latestY,
                    Stroke = seriesBrush,
                    StrokeThickness = 1.35,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    Opacity = 0.8,
                    IsHitTestVisible = false
                });
            }

            if (series.Count > 0)
            {
                var endpointMarker = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = seriesBrush,
                    Stroke = (Brush)FindResource("PlotBrush"),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(endpointMarker, latestX - 3);
                Canvas.SetTop(endpointMarker, latestY - 3);
                plotLayer.Children.Add(endpointMarker);
            }
        }

        _quotaCrosshair = new Line
        {
            Stroke = (Brush)FindResource("TextPrimaryBrush"),
            StrokeThickness = 1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.72,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        plotLayer.Children.Add(_quotaCrosshair);
        // Reset markers stay above the hover crosshair so the detected event
        // remains recognizable when the pointer is directly over the reset.
        if (!longRange)
        {
            AddQuotaResetMarkers(plotLayer, scale);
        }
        _quotaFiveHourInspectionMarker = CreateInspectionMarker("FiveHourBrush");
        _quotaWeeklyInspectionMarker = CreateInspectionMarker("WeeklyBrush");
        plotLayer.Children.Add(_quotaFiveHourInspectionMarker);
        plotLayer.Children.Add(_quotaWeeklyInspectionMarker);
        _quotaInspectionTooltipText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFontFamily"),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        _quotaInspectionTooltip = new Border
        {
            Background = (Brush)FindResource("SurfaceRaisedBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("TooltipCornerRadius"),
            Padding = (Thickness)FindResource("TooltipPadding"),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _quotaInspectionTooltipText
        };
        QuotaChart.Children.Add(_quotaInspectionTooltip);
        Panel.SetZIndex(_quotaInspectionTooltip, int.MaxValue);

        AddQuotaTimeLabels(plotLayer, scale, height - 18);
        if (_visibleQuotaHistory.Count == 0)
        {
            AddLabel(QuotaChart, "Select a quota series above", left + 8, top + 12, axisBrush, 12, plotWidth - 16);
        }
        else if (_visibleQuotaHistory.Count < 2)
        {
            AddLabel(QuotaChart, "Not enough quota history yet", left + 8, top + 12, axisBrush, 12, plotWidth - 16);
        }
    }

    internal static IReadOnlyList<QuotaCycleBar> BuildQuotaCycleBars(
        IReadOnlyList<QuotaHistoryPoint> points)
    {
        if (points.Count < 2)
        {
            return [];
        }

        var ordered = points
            .Where(point => point.Kind == "5-hour")
            .OrderBy(point => point.SampledAt)
            .ToArray();
        if (ordered.Length < 2)
        {
            return [];
        }

        var cycles = new List<List<QuotaHistoryPoint>>();
        var current = new List<QuotaHistoryPoint> { ordered[0] };
        var maximumCycleDataGap = TimeSpan.FromHours(5);
        var resetTimeTolerance = TimeSpan.FromMinutes(2);
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var point = ordered[index];
            var crossedReset = previous.ResetAt is { } resetAt
                && previous.SampledAt < resetAt
                && point.SampledAt >= resetAt;
            var resetEpochChanged = previous.ResetAt is { } previousReset
                && point.ResetAt is { } nextReset
                && (nextReset - previousReset).Duration() > resetTimeTolerance;
            var cycleWindowElapsed = point.SampledAt - current[0].SampledAt >= maximumCycleDataGap;
            if (crossedReset || resetEpochChanged || cycleWindowElapsed)
            {
                cycles.Add(current);
                current = [];
            }
            current.Add(point);
        }
        cycles.Add(current);

        return cycles
            .Where(cycle => cycle.Count >= 2)
            .Select(cycle => new QuotaCycleBar(
                cycle[0].ResetAt is { } nextReset
                    ? nextReset.AddHours(-2.5)
                    : cycle[0].SampledAt.AddSeconds((cycle[^1].SampledAt - cycle[0].SampledAt).TotalSeconds / 2),
                100 - cycle.Min(point => Math.Clamp(point.RemainingPercent, 0, 100)),
                cycle.Count,
                cycle[0].SampledAt,
                cycle[^1].SampledAt))
            .ToArray();
    }

    internal static string FormatQuotaCycleBarTooltip(QuotaCycleBar bar, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var first = bar.FirstSampleAt.ToLocalTime();
        var last = bar.LastSampleAt.ToLocalTime();
        var sampleRange = first.Date == last.Date
            ? $"{first.ToString("dd MMM · HH:mm", culture)}–{last.ToString("HH:mm", culture)}"
            : $"{first.ToString("dd MMM · HH:mm", culture)}–{last.ToString("dd MMM · HH:mm", culture)}";
        return $"5-hour cycle · {FormatQuotaPercent(bar.UsedPercent, culture)}% used\nObserved samples: {bar.SampleCount} · {sampleRange}";
    }

    internal static IReadOnlyList<QuotaHistoryPoint> GetQuotaInspectionSeries(
        int quotaHours,
        IReadOnlyList<QuotaHistoryPoint> fiveHour,
        IReadOnlyList<QuotaHistoryPoint> weekly) =>
        quotaHours >= 168
            ? weekly
            : fiveHour.Concat(weekly).OrderBy(point => point.SampledAt).ToArray();

    internal static IReadOnlyList<QuotaHistoryPoint> NormalizeQuotaHistory(
        IReadOnlyList<QuotaHistoryPoint> points)
    {
        var ordered = points
            .OrderBy(point => point.SampledAt)
            .ThenBy(point => point.Kind, StringComparer.Ordinal)
            .ToArray();
        var liveByKind = ordered
            .Where(point => string.Equals(point.Source, LiveQuotaSource, StringComparison.Ordinal))
            .GroupBy(point => point.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<QuotaHistoryPoint>)group.ToArray(), StringComparer.Ordinal);

        return ordered
            .Where(point =>
            {
                if (!string.Equals(point.Source, ImportedQuotaSource, StringComparison.Ordinal)
                    || !liveByKind.TryGetValue(point.Kind, out var livePoints))
                {
                    return true;
                }

                var nearestLive = FindNearestPoint(livePoints, point.SampledAt);
                return nearestLive is null
                    || Math.Abs((nearestLive.SampledAt - point.SampledAt).TotalSeconds)
                        > QuotaSourceDuplicateWindow.TotalSeconds;
            })
            .ToArray();
    }

    private static bool IsQuotaCycleBarSource(DependencyObject? source)
    {
        for (var current = source; current is not null;)
        {
            if (current is FrameworkElement { Tag: "quota-cycle-bar" })
            {
                return true;
            }

            current = current switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return false;
    }

    private void AddQuotaResetMarkers(Canvas plotLayer, QuotaChartScale scale)
    {
        var resets = FilterQuotaResetEvents(_visibleQuotaResetEvents, scale.StartAt, scale.EndAt);
        var resetGroups = GroupQuotaResetEvents(resets, TimeSpan.FromSeconds(5))
            .Select(group => new
            {
                Group = group,
                X = scale.MapX(group.EventAt)
            })
            .GroupBy(item => (int)Math.Floor((item.X - scale.Left) / 12.0))
            .Select(bucket => new
            {
                Items = bucket.ToArray(),
                X = bucket.Average(item => item.X)
            });
        foreach (var cluster in resetGroups)
        {
            var first = cluster.Items.MinBy(item => item.Group.EventAt)!.Group;
            var last = cluster.Items.MaxBy(item => item.Group.EventAt)!.Group;
            var kinds = cluster.Items.SelectMany(item => item.Group.Kinds)
                .Distinct(StringComparer.Ordinal).ToArray();
            var label = kinds.Length > 1 ? string.Join(" + ", kinds) : kinds[0];
            var resetLandingYs = cluster.Items
                .SelectMany(item => item.Group.Kinds.Select(kind =>
                {
                    var series = kind == "5-hour" ? _visibleFiveHourHistory : _visibleWeeklyHistory;
                    var point = FindNearestPoint(series, item.Group.EventAt);
                    return point is not null
                           && Math.Abs((point.SampledAt - item.Group.EventAt).TotalSeconds)
                               <= MaximumQuotaGap.TotalSeconds
                        ? (double?)scale.MapY(point.RemainingPercent)
                        : null;
                }))
                .Where(y => y.HasValue)
                .Select(y => y!.Value)
                .ToArray();
            var period = first.EventAt == last.EventAt
                ? first.EventAt.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture)
                : $"{first.EventAt.ToLocalTime().ToString("dd MMM", CultureInfo.CurrentCulture)} → {last.EventAt.ToLocalTime().ToString("dd MMM", CultureInfo.CurrentCulture)}";
            var count = cluster.Items.Sum(item => item.Group.Kinds.Count);
            var markerBrush = kinds.Length == 1
                ? (Brush)FindResource(kinds[0] == "5-hour" ? "FiveHourBrush" : "WeeklyBrush")
                : (Brush)FindResource("InteractionBrush");
            var line = new Line
            {
                X1 = cluster.X,
                Y1 = scale.Top,
                X2 = cluster.X,
                Y2 = scale.Bottom,
                Stroke = markerBrush,
                StrokeThickness = 1.35,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Opacity = Math.Min(0.96, 0.86 + count * 0.02),
                ToolTip = count == 1
                    ? $"Quota reset detected · {label} · {period}"
                    : $"{count} quota resets detected · {label} · {period}",
                IsHitTestVisible = true
            };
            plotLayer.Children.Add(line);

            // A compact cap makes the event easy to spot without relying on
            // the tooltip or on the reset jump in the quota line itself.
            var cap = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = markerBrush,
                Stroke = (Brush)FindResource("PlotBrush"),
                StrokeThickness = 1,
                ToolTip = line.ToolTip,
                IsHitTestVisible = true
            };
            Canvas.SetLeft(cap, cluster.X - 3);
            var capY = resetLandingYs.Length > 0
                ? resetLandingYs.Average()
                : scale.MapY(100);
            Canvas.SetTop(cap, capY - cap.Height / 2);
            plotLayer.Children.Add(cap);
        }
    }

    internal static IReadOnlyList<QuotaResetEventSnapshot> FilterQuotaResetEvents(
        IReadOnlyList<QuotaResetEventSnapshot> resets,
        DateTimeOffset startAt,
        DateTimeOffset endAt) =>
        resets.Where(reset => reset.EventAt >= startAt && reset.EventAt <= endAt).ToArray();

    internal static IReadOnlyList<QuotaResetEventSnapshot> ReconcileQuotaResetEvents(
        IReadOnlyList<QuotaHistoryPoint> history,
        IReadOnlyList<QuotaResetEventSnapshot> recordedEvents)
    {
        var inferred = new List<QuotaResetEventSnapshot>();
        foreach (var series in history
                     .Where(point => point.Kind is "5-hour" or "weekly")
                     .GroupBy(point => point.Kind, StringComparer.Ordinal))
        {
            var ordered = series.OrderBy(point => point.SampledAt).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.SampledAt - previous.SampledAt > MaximumQuotaGap)
                {
                    continue;
                }

                var increase = current.RemainingPercent - previous.RemainingPercent;
                var resetEpochChanged = previous.ResetAt is { } oldReset
                    && current.ResetAt is { } newReset
                    && (newReset - oldReset).Duration() > TimeSpan.FromMinutes(2);
                var crossedReset = previous.ResetAt is { } expectedReset
                    && expectedReset >= previous.SampledAt - TimeSpan.FromMinutes(2)
                    && expectedReset <= current.SampledAt;
                var clearRefill = increase >= 25 && current.RemainingPercent >= 90;
                var confirmedBoundary = increase >= 10
                    && current.RemainingPercent >= 75
                    && (resetEpochChanged || crossedReset);
                if (clearRefill || confirmedBoundary)
                {
                    inferred.Add(new QuotaResetEventSnapshot(current.SampledAt, series.Key));
                }
            }
        }

        var unmatchedRecorded = recordedEvents.Where(recorded =>
                !inferred.Any(detected => detected.Kind == recorded.Kind
                    && (detected.EventAt - recorded.EventAt).Duration() <= TimeSpan.FromMinutes(5)))
            .ToArray();

        // Keep inferred timestamps (sample time) when an alert record refers
        // to the same reset; the record is written slightly later by polling.
        var combined = inferred.Concat(unmatchedRecorded)
            .OrderBy(reset => reset.Kind, StringComparer.Ordinal)
            .ThenBy(reset => reset.EventAt)
            .ToArray();
        var deduplicated = new List<QuotaResetEventSnapshot>();
        foreach (var reset in combined)
        {
            if (deduplicated.Count > 0
                && deduplicated[^1].Kind == reset.Kind
                && (reset.EventAt - deduplicated[^1].EventAt).Duration() <= TimeSpan.FromMinutes(5))
            {
                continue;
            }

            deduplicated.Add(reset);
        }

        return deduplicated.OrderBy(reset => reset.EventAt).ToArray();
    }

    internal static IReadOnlyList<QuotaResetEventGroup> GroupQuotaResetEvents(
        IReadOnlyList<QuotaResetEventSnapshot> resets,
        TimeSpan mergeWindow)
    {
        if (mergeWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeWindow));
        }

        var ordered = resets.OrderBy(reset => reset.EventAt).ToArray();
        var groups = new List<QuotaResetEventGroup>();
        for (var index = 0; index < ordered.Length;)
        {
            var members = new List<QuotaResetEventSnapshot> { ordered[index++] };
            while (index < ordered.Length
                   && ordered[index].EventAt - members[0].EventAt <= mergeWindow)
            {
                members.Add(ordered[index++]);
            }

            groups.Add(new QuotaResetEventGroup(
                members[0].EventAt,
                members.Select(reset => reset.Kind).Distinct(StringComparer.Ordinal).ToArray()));
        }

        return groups;
    }

    private Ellipse CreateInspectionMarker(string brushKey) => new()
    {
        Width = 9,
        Height = 9,
        Fill = (Brush)FindResource(brushKey),
        Stroke = (Brush)FindResource("PlotBrush"),
        StrokeThickness = 2,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    private void AddQuotaTimeLabels(Canvas plotLayer, QuotaChartScale scale, double y)
    {
        if (_quotaHours >= 168)
        {
            var localStart = scale.StartAt.ToLocalTime();
            var tick = new DateTimeOffset(localStart.Date.AddDays(1), localStart.Offset);
            var targetSpacingDays = Math.Max(1, _quotaHours / 24.0 * 82 / Math.Max(1, scale.Width));
            var tickDays = new[] { 1, 2, 3, 5, 7, 10, 14 }
                .FirstOrDefault(days => days >= targetSpacingDays, 14);
            while (tick < scale.EndAt)
            {
                var x = scale.MapX(tick);
                AddLine(plotLayer, x, scale.Top, x, scale.Bottom, (Brush)FindResource("GridBrush"), 0.65);
                AddLabel(QuotaChart, tick.ToString("dd MMM", CultureInfo.CurrentCulture),
                    x - 27, y, (Brush)FindResource("TextSecondaryBrush"), 9, 54, centered: true);
                tick = tick.AddDays(tickDays);
            }
        }
        else
        {
            foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
            {
                var x = scale.Left + fraction * scale.Width - (fraction == 0 ? 0 : fraction == 1 ? 44 : 22);
                var label = scale.StartAt.AddSeconds((scale.EndAt - scale.StartAt).TotalSeconds * fraction)
                    .ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                AddLabel(QuotaChart, label, x, y, (Brush)FindResource("TextSecondaryBrush"), 9, 44);
            }
        }
    }

    private void QuotaChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_visibleQuotaHistory.Count == 0)
        {
            return;
        }

        if (_quotaHours >= 168 && IsQuotaCycleBarSource(e.OriginalSource as DependencyObject))
        {
            HideQuotaInspection();
            return;
        }

        var position = e.GetPosition(QuotaChart);
        var end = _snapshot!.ReadAt;
        var start = end.AddHours(-_quotaHours);
        var scale = new QuotaChartScale(
            start,
            end,
            42,
            27,
            Math.Max(1, QuotaChart.ActualWidth - 54),
            Math.Max(1, QuotaChart.ActualHeight - 55));
        var target = scale.TimeAt(position.X);
        var inspectionSeries = GetQuotaInspectionSeries(
            _quotaHours, _visibleFiveHourHistory, _visibleWeeklyHistory);
        var nearest = FindNearestPoint(inspectionSeries, target);
        if (nearest is null)
        {
            HideQuotaInspection();
            return;
        }

        var x = scale.MapX(nearest.SampledAt);
        if (_quotaCrosshair is not null)
        {
            _quotaCrosshair.X1 = x;
            _quotaCrosshair.X2 = x;
            _quotaCrosshair.Y1 = scale.Top + 1;
            _quotaCrosshair.Y2 = scale.Bottom - 1;
            _quotaCrosshair.Visibility = Visibility.Visible;
        }

        var tooltip = new List<string>
        {
            nearest.SampledAt.ToLocalTime().ToString("dd MMM · HH:mm:ss", CultureInfo.CurrentCulture)
        };
        var nearestSeries = nearest.Kind == "5-hour" ? _visibleFiveHourHistory : _visibleWeeklyHistory;
        if (nearestSeries.Count > 0
            && target > nearestSeries[^1].SampledAt
            && target - nearestSeries[^1].SampledAt > MaximumQuotaGap)
        {
            tooltip.Add("No newer reading · dashed continuation");
        }
        else if (Math.Abs((target - nearest.SampledAt).TotalSeconds) > MaximumQuotaGap.TotalSeconds)
        {
            tooltip.Add("No reading at this time · chart gap");
        }
        var inspectionKinds = _quotaHours >= 168
            ? new[] { "weekly" }
            : new[] { "5-hour", "weekly" };
        foreach (var kind in inspectionKinds)
        {
            var series = kind == "5-hour" ? _visibleFiveHourHistory : _visibleWeeklyHistory;
            var related = FindNearestPoint(series, target);
            var marker = kind == "5-hour" ? _quotaFiveHourInspectionMarker : _quotaWeeklyInspectionMarker;
            if (related is not null
                && Math.Abs((related.SampledAt - target).TotalSeconds) <= MaximumQuotaGap.TotalSeconds)
            {
                var time = related.SampledAt == nearest.SampledAt
                    ? string.Empty
                    : $" · {related.SampledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";
                tooltip.Add($"{kind}: {FormatQuotaPercent(related.RemainingPercent)}% left{time}");
                if (marker is not null)
                {
                    var markerPoint = scale.Map(related.SampledAt, related.RemainingPercent);
                    Canvas.SetLeft(marker, markerPoint.X - marker.Width / 2);
                    Canvas.SetTop(marker, markerPoint.Y - marker.Height / 2);
                    marker.Visibility = Visibility.Visible;
                }
            }
            else if (marker is not null)
            {
                marker.Visibility = Visibility.Collapsed;
                tooltip.Add(series.Count > 0 && nearest.SampledAt > series[^1].SampledAt
                    ? $"{kind}: no newer reading"
                    : $"{kind}: no reading at this time");
            }
        }

        if (_quotaInspectionTooltip is not null && _quotaInspectionTooltipText is not null)
        {
            _quotaInspectionTooltipText.Text = string.Join(Environment.NewLine, tooltip);
            _quotaInspectionTooltip.MaxWidth = Math.Max(1, QuotaChart.ActualWidth - 12);
            _quotaInspectionTooltip.Measure(new Size(
                Math.Max(1, QuotaChart.ActualWidth - 12),
                Math.Max(1, QuotaChart.ActualHeight - 12)));
            var tooltipPosition = PlaceQuotaInspectionTooltip(
                position,
                _quotaInspectionTooltip.DesiredSize,
                new Size(QuotaChart.ActualWidth, QuotaChart.ActualHeight));
            Canvas.SetLeft(_quotaInspectionTooltip, tooltipPosition.X);
            Canvas.SetTop(_quotaInspectionTooltip, tooltipPosition.Y);
            _quotaInspectionTooltip.Visibility = Visibility.Visible;
        }
    }

    internal static Point PlaceQuotaInspectionTooltip(Point cursor, Size tooltipSize, Size chartSize)
    {
        const double margin = 6;
        const double offset = 12;
        var left = cursor.X + offset;
        if (left + tooltipSize.Width > chartSize.Width - margin)
        {
            left = cursor.X - tooltipSize.Width - offset;
        }
        var top = cursor.Y - tooltipSize.Height - offset;
        if (top < margin)
        {
            top = cursor.Y + offset;
        }

        var maxLeft = Math.Max(margin, chartSize.Width - tooltipSize.Width - margin);
        var maxTop = Math.Max(margin, chartSize.Height - tooltipSize.Height - margin);
        return new Point(
            Math.Clamp(left, margin, maxLeft),
            Math.Clamp(top, margin, maxTop));
    }

    internal static QuotaHistoryPoint? FindNearestPoint(
        IReadOnlyList<QuotaHistoryPoint> points,
        DateTimeOffset target)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var low = 0;
        var high = points.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].SampledAt < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low == 0)
        {
            return points[0];
        }
        if (low == points.Count)
        {
            return points[^1];
        }

        var before = points[low - 1];
        var after = points[low];
        return target - before.SampledAt <= after.SampledAt - target ? before : after;
    }

    internal static string FormatQuotaPercent(double remainingPercent, CultureInfo? culture = null) =>
        remainingPercent.ToString("G", culture ?? CultureInfo.CurrentCulture);

    private void QuotaChart_MouseLeave(object sender, MouseEventArgs e)
    {
        HideQuotaInspection();
    }

    private void HideQuotaInspection()
    {
        if (_quotaInspectionTooltip is not null)
        {
            _quotaInspectionTooltip.Visibility = Visibility.Collapsed;
        }
        if (_quotaCrosshair is not null)
        {
            _quotaCrosshair.Visibility = Visibility.Collapsed;
        }
        if (_quotaFiveHourInspectionMarker is not null)
        {
            _quotaFiveHourInspectionMarker.Visibility = Visibility.Collapsed;
        }
        if (_quotaWeeklyInspectionMarker is not null)
        {
            _quotaWeeklyInspectionMarker.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderDailyHistory()
    {
        DailyChart.Children.Clear();
        DailyHeatmap.Children.Clear();
        if (_history.Count == 0 || DailyChart.ActualWidth < 80 || DailyChart.ActualHeight < 60)
        {
            return;
        }

        var width = DailyChart.ActualWidth;
        var height = DailyChart.ActualHeight;
        const double left = 38;
        const double right = 8;
        const double top = 15;
        const double labelHeight = 27;
        var plotBottom = height - labelHeight;
        var plotHeight = Math.Max(1, plotBottom - top);
        var plotWidth = Math.Max(1, width - left - right);
        var maxTotal = _history.Max(day => day.Total);
        var scaleMax = Math.Max(1, maxTotal);
        var axisBrush = (Brush)FindResource("TextSecondaryBrush");

        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var y = top + plotHeight * (1 - fraction);
            AddLine(DailyChart, left, y, width - right, y, (Brush)FindResource("GridBrush"), 0.7);
            var label = FormatCompact((long)Math.Round(scaleMax * fraction, MidpointRounding.ToEven));
            AddLabel(DailyChart, label, 1, y - 8, axisBrush, 9, left - 5);
        }

        var step = plotWidth / _history.Count;
        var barWidth = Math.Max(2, step * 0.64);
        var labelStep = _history.Count <= 14 ? 1 : Math.Max(1, (_history.Count + 5) / 6);
        for (var i = 0; i < _history.Count; i++)
        {
            var day = _history[i];
            var barHeight = maxTotal == 0 ? 0 : day.Total / (double)scaleMax * plotHeight;
            var x = left + step * i + (step - barWidth) / 2;
            if (day.Total > 0)
            {
                var isToday = day.LocalDay == _snapshot?.Today.LocalDay;
                var isSelected = day.LocalDay == _selectedHistoryDay;
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(1, barHeight),
                    Fill = (Brush)FindResource(isSelected && !isToday
                        ? "InteractionBrush"
                        : isToday ? "TodayBrush" : "UsageHistoryBrush"),
                    RadiusX = 2,
                    RadiusY = 2,
                    ToolTip = $"{day.LocalDay.ToDateTime(TimeOnly.MinValue).ToString("d MMM", CultureInfo.CurrentCulture)}\n{Format(day.Total)} tokens",
                    IsHitTestVisible = true
                };
                var defaultFill = bar.Fill;
                bar.Cursor = Cursors.Hand;
                bar.MouseEnter += (_, _) =>
                {
                    bar.Fill = (Brush)FindResource(isToday
                        ? "TodayBrush"
                        : isSelected ? "InteractionBrush" : "UsageBrush");
                    bar.Stroke = (Brush)FindResource("TextPrimaryBrush");
                    bar.StrokeThickness = 1;
                };
                bar.MouseLeave += (_, _) =>
                {
                    bar.Fill = defaultFill;
                    bar.Stroke = null;
                    bar.StrokeThickness = 0;
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, plotBottom - bar.Height);
                DailyChart.Children.Add(bar);

            }

            if (i % labelStep == 0 || i == _history.Count - 1)
            {
                var label = i == _history.Count - 1
                    ? "Today"
                    : day.LocalDay.ToDateTime(TimeOnly.MinValue).ToString("dd MMM", CultureInfo.CurrentCulture);
                var labelWidth = _history.Count <= 14 ? step * 1.5 : 54;
                var labelCenter = x + barWidth / 2;
                var labelX = labelCenter - labelWidth / 2;
                AddLabel(DailyChart, label, labelX, plotBottom + 4,
                    i == _history.Count - 1 ? (Brush)FindResource("TodayBrush") : axisBrush,
                    8, labelWidth, centered: true);
            }
        }

        RenderDailyHeatmap();
        UpdateDailySummary();
    }

    private void RenderDailyHeatmap()
    {
        DailyHeatmap.Children.Clear();
        if (_history.Count == 0 || DailyHeatmap.ActualWidth < 80)
        {
            return;
        }

        var ordered = _history.Select(day => day.Total).OrderBy(total => total).ToArray();
        var quantile = Math.Max(1, ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * 0.9))]);
        var width = DailyHeatmap.ActualWidth;
        const double left = 38;
        const double right = 8;
        const double gap = 2;
        var step = Math.Max(1, (width - left - right) / _history.Count);
        var cellWidth = Math.Max(2, step - gap);
        var showValues = _historyDays is 7 or 14;

        for (var i = 0; i < _history.Count; i++)
        {
            var day = _history[i];
            var intensity = Math.Clamp(day.Total / (double)quantile, 0, 1);
            var isToday = day.LocalDay == _snapshot?.Today.LocalDay;
            var isSelected = day.LocalDay == _selectedHistoryDay;
            var fill = isToday
                ? (Brush)FindResource("TodayBrush")
                : isSelected
                    ? (Brush)FindResource("InteractionBrush")
                    : HeatBrush(intensity);
            var cellX = left + i * step + gap / 2;
            const double cellHeight = 20;
            var cell = new Border
            {
                Width = cellWidth,
                Height = cellHeight,
                Background = fill,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(cell, cellX);
            Canvas.SetTop(cell, 0);
            DailyHeatmap.Children.Add(cell);

            if (showValues)
            {
                var value = new TextBlock
                {
                    Text = FormatCompact(day.Total),
                    FontFamily = (FontFamily)FindResource("UiFontFamily"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isToday
                        || isSelected
                        || intensity >= 0.55
                            ? (Brush)FindResource("WindowBrush")
                            : (Brush)FindResource("TextPrimaryBrush"),
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextWrapping = TextWrapping.NoWrap,
                    IsHitTestVisible = false
                };
                Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
                cell.Child = value;
            }
        }

        AddLabel(DailyHeatmap, "LOW", 1, 22, (Brush)FindResource("TextSecondaryBrush"), 8, 30);
        AddLabel(DailyHeatmap, "HIGH · robust daily scale", left, 22, (Brush)FindResource("TextSecondaryBrush"), 8, 190);
    }

    private Brush HeatBrush(double intensity)
    {
        var alpha = (byte)Math.Clamp(45 + 210 * intensity, 45, 255);
        var usage = ((SolidColorBrush)FindResource("UsageBrush")).Color;
        return new SolidColorBrush(Color.FromArgb(alpha, usage.R, usage.G, usage.B));
    }

    private void UpdateDailySummary()
    {
        if (_snapshot is null || _history.Count == 0)
        {
            DailyHistorySummaryText.Text = "No daily history available";
            return;
        }

        if (_selectedHistoryDay is not null)
        {
            var selected = _history.First(day => day.LocalDay == _selectedHistoryDay);
            DailyHistorySummaryText.Text =
                $"{selected.LocalDay.ToDateTime(TimeOnly.MinValue).ToString("d MMM", CultureInfo.CurrentCulture)} · Total {Format(selected.Total)} · Input {Format(selected.Input)} · Cached {Format(selected.Cached)} · Output {Format(selected.Output)} · Reasoning {Format(selected.Reasoning)}";
            return;
        }

        var today = _snapshot.Today.Total;
        var yesterday = _history.Count > 1 ? _history[^2].Total : 0;
        var lastSeven = _history.TakeLast(7).ToArray();
        var average = lastSeven.Length == 0 ? 0 : lastSeven.Average(day => (double)day.Total);
        var change = yesterday == 0
            ? "n/a"
            : ((today - yesterday) * 100.0 / yesterday).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "%";
        DailyHistorySummaryText.Text = $"Today {Format(today)} · Yesterday {Format(yesterday)} · 7-day average {Format((long)Math.Round(average, MidpointRounding.ToEven))} · Change {change}";
    }

    private void DailyChart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectHistoryDay(e.GetPosition(DailyChart).X, DailyChart.ActualWidth);

    private void DailyHeatmap_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectHistoryDay(e.GetPosition(DailyHeatmap).X, DailyHeatmap.ActualWidth);

    private void SelectHistoryDay(double x, double width)
    {
        if (_history.Count == 0)
        {
            return;
        }

        const double left = 38;
        const double right = 8;
        var index = Math.Clamp((int)((x - left) / Math.Max(1, width - left - right) * _history.Count), 0, _history.Count - 1);
        _selectedHistoryDay = _history[index].LocalDay;
        RenderDailyHistory();
    }

    private void DailyChart_MouseMove(object sender, MouseEventArgs e) =>
        ShowDailyTooltip(e.GetPosition(DailyChart).X, DailyChart.ActualWidth);

    private void DailyHeatmap_MouseMove(object sender, MouseEventArgs e) =>
        ShowDailyTooltip(e.GetPosition(DailyHeatmap).X, DailyHeatmap.ActualWidth);

    private void ShowDailyTooltip(double x, double width)
    {
        if (_history.Count == 0)
        {
            return;
        }

        const double left = 38;
        const double right = 8;
        var index = Math.Clamp((int)((x - left) / Math.Max(1, width - left - right) * _history.Count), 0, _history.Count - 1);
        var day = _history[index];
        DailyChart.ToolTip = $"{day.LocalDay.ToDateTime(TimeOnly.MinValue).ToString("d MMM", CultureInfo.CurrentCulture)}\n{Format(day.Total)} tokens\nClick to inspect day";
        ToolTipService.SetShowDuration(DailyChart, 10000);
    }

    private void DailyChart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            canvas.ToolTip = null;
        }
    }

    private void QuotaChart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderQuotaChart();

    private void DailyChart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderDailyHistory();

    private void ShowUnavailable(string message)
    {
        _snapshot = null;
        _compactWindow?.UpdateValues(null, _quotaSourceUnavailable);
        _history = [];
        _visibleQuotaHistory = [];
        FiveHourValueText.Text = "—";
        WeeklyValueText.Text = "—";
        TodayValueText.Text = "—";
        TodayValueText.ToolTip = null;
        AutomationProperties.SetHelpText(TodayValueText, string.Empty);
        FiveHourDetailText.Text = "Quota data unavailable";
        WeeklyDetailText.Text = "Quota data unavailable";
        ReserveValueText.Text = "—";
        ReserveDetailText.Text = "Quota data unavailable";
        TodayDetailText.Text = "No values estimated";
        FiveHourProgress.Value = 0;
        WeeklyProgress.Value = 0;
        ReserveProgress.Value = 0;
        _lastHistoryReadAt = null;
        _quotaChartNeedsRender = true;
        _dailyChartNeedsRender = true;
        TodaySparkline.Children.Clear();
        QuotaChart.Children.Clear();
        DailyChart.Children.Clear();
        DailyHeatmap.Children.Clear();
        QuotaChart.Opacity = 1;
        DailyChart.Opacity = 1;
        DailyHistorySummaryText.Text = "History unavailable";
        SetRefreshStatus(message, _manualRefreshInProgress);
        ApplyUpdatedStatus(null, Array.Empty<QuotaUsageSnapshot>(), DateTimeOffset.UtcNow,
            allQuotasFromLiveSource: false);
    }

    private string Forecast(
        IReadOnlyList<QuotaHistoryPoint> history,
        string kind,
        DateTimeOffset? reset,
        DateTimeOffset now,
        int hours,
        bool isWeekly)
    {
        var points = history
            .Where(point => point.Kind == kind && point.SampledAt >= now.AddHours(-hours) && point.SampledAt <= now)
            .OrderBy(point => point.SampledAt)
            .TakeLast(20)
            .ToArray();
        if (points.Length < 3)
        {
            return "Burn: insufficient data";
        }

        var windowSeconds = isWeekly ? 86400.0 : 3600.0;
        var slopes = points.Zip(points.Skip(1), (a, b) =>
            (b.SampledAt - a.SampledAt).TotalSeconds >= 300
                ? (b.RemainingPercent - a.RemainingPercent) / (b.SampledAt - a.SampledAt).TotalSeconds * windowSeconds
                : (double?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        if (slopes.Length == 0)
        {
            return "Burn: insufficient data";
        }

        var rate = slopes.Length % 2 == 1
            ? slopes[slopes.Length / 2]
            : (slopes[slopes.Length / 2 - 1] + slopes[slopes.Length / 2]) / 2;
        var unit = isWeekly ? "pp/day" : "pp/hour";
        if (Math.Abs(rate) < 0.1)
        {
            return "Burn: Stable";
        }
        if (rate >= 0)
        {
            return $"Recovery: +{rate.ToString("0.0", CultureInfo.CurrentCulture)} {unit}";
        }

        var latestRemaining = points[^1].RemainingPercent;
        var estimateHours = latestRemaining <= 0 ? (double?)null : latestRemaining / -rate * (isWeekly ? 24 : 1);
        if (estimateHours is null || (reset is not null && reset > now && reset <= now.AddHours(estimateHours.Value)))
        {
            return reset is not null && reset > now
                ? $"Burn: {rate.ToString("0.0", CultureInfo.CurrentCulture)} {unit} · No exhaustion before reset"
                : $"Burn: {rate.ToString("0.0", CultureInfo.CurrentCulture)} {unit} · Not enough recent data";
        }

        return isWeekly
            ? $"Burn: {rate.ToString("0.0", CultureInfo.CurrentCulture)} {unit} · ~{(estimateHours.Value / 24).ToString("0.0", CultureInfo.CurrentCulture)}d to 0%"
            : $"Burn: {rate.ToString("0.0", CultureInfo.CurrentCulture)} {unit} · ~{estimateHours.Value.ToString("0.0", CultureInfo.CurrentCulture)}h to 0%";
    }

    internal static string ResetSummary(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null)
        {
            return "Reset time unavailable";
        }

        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset passed · waiting for a new update";
        }

        var minutes = Math.Max(1, (int)remaining.TotalMinutes);
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        var duration = days > 0
            ? $"{days}d {hours}h {mins}m"
            : hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
        var localReset = reset.Value.ToLocalTime();
        var localNow = now.ToLocalTime();
        var absolute = localReset.Date == localNow.Date
            ? $"Today · {localReset.ToString("HH:mm", CultureInfo.CurrentCulture)}"
            : localReset.Date == localNow.Date.AddDays(1)
                ? $"Tomorrow · {localReset.ToString("HH:mm", CultureInfo.CurrentCulture)}"
                : localReset.ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture);
        return $"Resets in {duration}\n{absolute}";
    }

    internal static IReadOnlyList<QuotaHistoryPoint> Downsample(IReadOnlyList<QuotaHistoryPoint> points, int limit)
    {
        if (points.Count <= limit)
        {
            return points;
        }

        var bucketCount = Math.Max(1, limit / 2);
        var reduced = new List<QuotaHistoryPoint>();
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * points.Count / bucketCount;
            var end = (bucket + 1) * points.Count / bucketCount;
            var minimum = points[start];
            var maximum = minimum;
            for (var index = start + 1; index < end; index++)
            {
                var point = points[index];
                if (point.RemainingPercent < minimum.RemainingPercent)
                {
                    minimum = point;
                }
                if (point.RemainingPercent > maximum.RemainingPercent)
                {
                    maximum = point;
                }
            }

            if (minimum == maximum)
            {
                reduced.Add(minimum);
            }
            else if (minimum.SampledAt <= maximum.SampledAt)
            {
                reduced.Add(minimum);
                reduced.Add(maximum);
            }
            else
            {
                reduced.Add(maximum);
                reduced.Add(minimum);
            }
        }

        return reduced;
    }

    internal static IReadOnlyList<IReadOnlyList<QuotaHistoryPoint>> SplitQuotaHistoryAtGaps(
        IReadOnlyList<QuotaHistoryPoint> points,
        TimeSpan maximumGap)
    {
        if (maximumGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGap));
        }
        if (points.Count == 0)
        {
            return [];
        }

        var segments = new List<IReadOnlyList<QuotaHistoryPoint>>();
        var current = new List<QuotaHistoryPoint> { points[0] };
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            if (point.SampledAt - current[^1].SampledAt > maximumGap)
            {
                segments.Add(current.ToArray());
                current.Clear();
            }
            current.Add(point);
        }

        segments.Add(current.ToArray());
        return segments;
    }

    internal static IReadOnlyList<QuotaGapBridge> FindQuotaGapBridges(
        IReadOnlyList<QuotaHistoryPoint> points,
        TimeSpan maximumGap)
    {
        if (maximumGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGap));
        }

        return points.Zip(points.Skip(1), (from, to) => new QuotaGapBridge(from, to))
            .Where(gap => gap.To.SampledAt - gap.From.SampledAt > maximumGap)
            .ToArray();
    }

    internal static string FitDailyValueLabel(long value, double availableWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (availableWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        }

        var full = Format(value);
        if (measure(full) + 4 <= availableWidth)
        {
            return full;
        }

        var compact = FormatCompact(value);
        if (measure(compact) + 4 <= availableWidth)
        {
            return compact;
        }
        return FormatCompactInteger(value);
    }

    internal static bool ShouldPlaceDailyValueInside(double barHeight, double measuredLabelHeight, double safePadding) =>
        barHeight >= measuredLabelHeight + safePadding;

    internal static bool ShouldRenderQuotaStaleTail(double tailLength) =>
        tailLength >= MinVisibleQuotaStaleTail;

    private static string FormatQuotaGap(TimeSpan gap) => gap.TotalDays >= 1
        ? $"{(int)gap.TotalDays}d {gap.Hours}h"
        : gap.TotalHours >= 1
            ? $"{(int)gap.TotalHours}h {gap.Minutes}m"
            : $"{Math.Max(1, (int)gap.TotalMinutes)}m";

    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        canvas.Children.Add(line);
    }

    private void AddLabel(Canvas canvas, string text, double x, double y, Brush brush, double fontSize, double width,
        bool centered = false)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = Math.Max(1, width),
            FontFamily = (FontFamily)FindResource("UiFontFamily"),
            FontSize = fontSize,
            Foreground = brush,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatCompact(long value) => value switch
    {
        >= 1_000_000_000 => $"{(value / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)}B",
        >= 1_000_000 => $"{(value / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)}M",
        >= 1_000 => $"{(value / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture)}K",
        _ => value.ToString(CultureInfo.CurrentCulture)
    };

    private static string FormatCompactInteger(long value) => value switch
    {
        >= 1_000_000_000 => $"{(value / 1_000_000_000.0).ToString("0", CultureInfo.CurrentCulture)}B",
        >= 1_000_000 => $"{(value / 1_000_000.0).ToString("0", CultureInfo.CurrentCulture)}M",
        >= 1_000 => $"{(value / 1_000.0).ToString("0", CultureInfo.CurrentCulture)}K",
        _ => value.ToString(CultureInfo.CurrentCulture)
    };
}
