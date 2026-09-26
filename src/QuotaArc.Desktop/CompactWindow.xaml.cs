using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Shell;
using System.Windows.Threading;

namespace QuotaArc.Desktop;

public partial class CompactWindow : Window
{
    private const double DefaultWidth = 220;
    private const double DefaultHeight = 56;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const int HtCaption = 2;
    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;
    private readonly MainWindow _owner;
    private bool _closeFromOwner;
    private bool _hoverActive;
    private bool _trackingClientLeave;
    private bool _trackingNonClientLeave;
    private HwndSource? _windowSource;
    private DashboardUsageSnapshot? _lastDisplayedSnapshot;
    private bool _hasDisplayedSnapshot;

    public CompactWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Loaded += (_, _) =>
        {
            UpdatePanelClip();
            UpdateHoverState(false);
        };
        SourceInitialized += (_, _) =>
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowProcedure);
        };
        Closed += (_, _) => _windowSource?.RemoveHook(WindowProcedure);

        if (WindowPositionStore.TryLoad("compact", MinWidth, MinHeight, out var position, out var size))
        {
            Left = position.X;
            Top = position.Y;
            if (!size.IsEmpty)
            {
                Width = size.Width;
                Height = size.Height;
            }
        }

        LocationChanged += (_, _) => SavePosition();
        SizeChanged += (_, _) =>
        {
            UpdatePanelClip();
            SavePosition();
        };
        Closing += (_, e) =>
        {
            SavePosition();
            if (!_closeFromOwner)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void UpdateValues(DashboardUsageSnapshot? snapshot, bool quotaSourceOffline = false)
    {
        if (snapshot is null)
        {
            SetUnavailable("5-hour", FiveHourValueText, PreviousFiveHourValueText,
                FiveHourProgress, FiveHourGhostMarker, FiveHourGhostTransform,
                FiveHourSweep, FiveHourSweepTransform, FiveHourRow, quotaSourceOffline);
            SetUnavailable("Weekly", WeeklyValueText, PreviousWeeklyValueText,
                WeeklyProgress, WeeklyGhostMarker, WeeklyGhostTransform,
                WeeklySweep, WeeklySweepTransform, WeeklyRow, false);
            SetReserveRow(null, default, allowMotion: false, previousQuota: null);
            _lastDisplayedSnapshot = null;
            _hasDisplayedSnapshot = false;
            return;
        }

        var previous = _lastDisplayedSnapshot;
        var allowMotion = _hasDisplayedSnapshot && IsVisible && !quotaSourceOffline
            && SystemParameters.ClientAreaAnimation;
        var fiveHourReset = IsNewReset(previous, snapshot, "5-hour", previous?.FiveHour, snapshot.FiveHour);
        var weeklyReset = IsNewReset(previous, snapshot, "weekly", previous?.Weekly, snapshot.Weekly);

        SetQuotaRow("5-hour", snapshot.FiveHour, previous?.FiveHour, FiveHourValueText,
            PreviousFiveHourValueText, FiveHourProgress, FiveHourRow, FiveHourGhostMarker,
            FiveHourGhostTransform, FiveHourSweep, FiveHourSweepTransform, snapshot.ReadAt,
            quotaSourceOffline, showOffline: true, allowMotion: allowMotion,
            sampleIsNew: IsNewSample(previous?.FiveHour, snapshot.FiveHour), isReset: fiveHourReset);
        SetQuotaRow("Weekly", snapshot.Weekly, previous?.Weekly, WeeklyValueText,
            PreviousWeeklyValueText, WeeklyProgress, WeeklyRow, WeeklyGhostMarker,
            WeeklyGhostTransform, WeeklySweep, WeeklySweepTransform, snapshot.ReadAt,
            quotaSourceOffline, showOffline: true, allowMotion: allowMotion,
            sampleIsNew: IsNewSample(previous?.Weekly, snapshot.Weekly), isReset: weeklyReset);
        SetReserveRow(snapshot.Reserve, snapshot.ReadAt, allowMotion, previous?.Reserve);
        _lastDisplayedSnapshot = snapshot;
        _hasDisplayedSnapshot = true;
    }

    public void HideAndSave()
    {
        UpdateHoverState(false);
        SavePosition();
        Hide();
    }

    public void CloseFromOwner()
    {
        _closeFromOwner = true;
        Close();
    }

    private void Main_Click(object sender, RoutedEventArgs e) => _owner.ActivateFromMini();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideAndSave();

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmNcLButtonDblClk && wParam.ToInt32() == HtCaption)
        {
            handled = true;
            _owner.ActivateFromMini();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case WmNcMouseMove:
            case WmNcLButtonDown:
                UpdateHoverState(true);
                TrackMouseLeave(hwnd, clientArea: false);
                break;
            case WmMouseMove:
                UpdateHoverState(true);
                TrackMouseLeave(hwnd, clientArea: true);
                break;
            case WmNcMouseLeave:
                _trackingNonClientLeave = false;
                UpdateHoverState(IsCursorInsideWindow(hwnd));
                if (_hoverActive)
                {
                    TrackMouseLeave(hwnd, clientArea: true);
                }
                break;
            case WmMouseLeave:
                _trackingClientLeave = false;
                UpdateHoverState(IsCursorInsideWindow(hwnd));
                if (_hoverActive)
                {
                    TrackMouseLeave(hwnd, clientArea: false);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void TrackMouseLeave(IntPtr hwnd, bool clientArea)
    {
        if (clientArea ? _trackingClientLeave : _trackingNonClientLeave)
        {
            return;
        }

        var tracking = new TrackMouseEventData
        {
            Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
            Flags = TmeLeave | (clientArea ? 0 : TmeNonClient),
            TrackWindow = hwnd
        };
        if (TrackMouseEvent(ref tracking))
        {
            if (clientArea)
            {
                _trackingClientLeave = true;
            }
            else
            {
                _trackingNonClientLeave = true;
            }
        }
    }

    private static bool IsCursorInsideWindow(IntPtr hwnd)
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var bounds))
        {
            return false;
        }

        return cursor.X >= bounds.Left && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
    }

    private void UpdatePanelClip()
    {
        var width = PanelContent.ActualWidth;
        var height = PanelContent.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (WindowChrome.GetWindowChrome(this) is { } chrome
            && Math.Abs(chrome.CaptionHeight - height) > 0.1)
        {
            chrome.CaptionHeight = height;
        }

        PanelContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 7, 7);
    }

    private void UpdateHoverState(bool shouldShow)
    {
        if (_hoverActive == shouldShow)
        {
            return;
        }

        _hoverActive = shouldShow;
        ActionRail.IsHitTestVisible = shouldShow;
        AnimateOpacity(ActionRail, shouldShow ? 1 : 0, shouldShow ? 125 : 167);
        AnimateOpacity(FiveHourProgress, shouldShow ? 1 : 0.94, shouldShow ? 150 : 190);
        AnimateOpacity(WeeklyProgress, shouldShow ? 1 : 0.94, shouldShow ? 150 : 190);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr TrackWindow;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData tracking);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    private void AnimateOpacity(UIElement element, double target, int durationMilliseconds)
    {
        var from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
        if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - target) < 0.001)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    public void ResetPositionToPrimaryDisplay()
    {
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal;
        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = workArea.Left + 24;
        Top = workArea.Top + 24;
        SavePosition();
    }

    private void SavePosition() => WindowPositionStore.Save("compact", Left, Top, Width, Height);

    private void SetReserveRow(
        QuotaUsageSnapshot? quota,
        DateTimeOffset readAt,
        bool allowMotion,
        QuotaUsageSnapshot? previousQuota)
    {
        if (quota is null)
        {
            ReserveProgress.BeginAnimation(RangeBase.ValueProperty, null);
            ReserveProgress.Value = 0;
            ReserveRow.ToolTip = "Reserve quota data unavailable.";
            return;
        }

        var remaining = Math.Clamp(100 - quota.UsedPercent, 0, 100);
        var fresh = QuotaFreshness.GetState(quota, readAt) == QuotaSampleState.Current;
        TransitionProgress(ReserveProgress, remaining,
            animate: allowMotion && previousQuota is not null && fresh,
            ghostMarker: null, ghostTransform: null, reset: false);
        ReserveRow.ToolTip = $"Reserve quota · {remaining.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)}% left\n{MainWindow.ResetSummary(quota.ResetAt, readAt)}";
    }

    private void SetQuotaRow(
        string label,
        QuotaUsageSnapshot? quota,
        QuotaUsageSnapshot? previousQuota,
        TextBlock valueText,
        TextBlock previousValueText,
        ProgressBar progress,
        FrameworkElement row,
        Border ghostMarker,
        TranslateTransform ghostTransform,
        Border sweep,
        TranslateTransform sweepTransform,
        DateTimeOffset readAt,
        bool sourceOffline,
        bool showOffline,
        bool allowMotion,
        bool sampleIsNew,
        bool isReset)
    {
        if (quota is null)
        {
            SetUnavailable(label, valueText, previousValueText, progress, ghostMarker, ghostTransform,
                sweep, sweepTransform, row, sourceOffline && showOffline);
            return;
        }

        var remaining = Math.Clamp(100 - quota.UsedPercent, 0, 100);
        var formatted = FormatRemaining(quota);
        var freshness = QuotaFreshness.GetState(quota, readAt);
        var fresh = freshness == QuotaSampleState.Current;
        valueText.Foreground = !sourceOffline && fresh
            ? (Brush)FindResource("TextPrimaryBrush")
            : (Brush)FindResource("MiniStatusBrush");
        var transitionAllowed = allowMotion && previousQuota is not null && fresh && valueText.Text != "—";
        TransitionText(valueText, previousValueText, formatted, transitionAllowed);
        TransitionProgress(progress, remaining, transitionAllowed, ghostMarker, ghostTransform,
            isReset && transitionAllowed);
        var isDecrease = previousQuota is not null
            && remaining < Math.Clamp(100 - previousQuota.UsedPercent, 0, 100) - 0.05;
        if (transitionAllowed && sampleIsNew && !isReset && !isDecrease && row.ActualWidth > 0)
        {
            AnimateUpdateSweep(sweep, sweepTransform, row);
        }
        else
        {
            StopUpdateSweep(sweep, sweepTransform);
        }

        AutomationProperties.SetName(valueText, $"{label} quota {formatted} left");
        var freshnessDetail = QuotaFreshness.FormatSampleDetail(quota, readAt, !sourceOffline);
        row.ToolTip = $"{label} quota · {formatted} left\n{MainWindow.ResetSummary(quota.ResetAt, readAt)}\n{freshnessDetail}\nSource: {quota.Source}";
    }

    private void SetUnavailable(
        string label,
        TextBlock valueText,
        TextBlock previousValueText,
        ProgressBar progress,
        Border ghostMarker,
        TranslateTransform ghostTransform,
        Border sweep,
        TranslateTransform sweepTransform,
        FrameworkElement row,
        bool offline)
    {
        progress.BeginAnimation(RangeBase.ValueProperty, null);
        progress.Value = 0;
        valueText.BeginAnimation(UIElement.OpacityProperty, null);
        valueText.Text = "—";
        valueText.Foreground = (Brush)FindResource("TextMutedBrush");
        valueText.Opacity = 1;
        previousValueText.BeginAnimation(UIElement.OpacityProperty, null);
        previousValueText.Text = string.Empty;
        previousValueText.Opacity = 0;
        previousValueText.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(valueText, $"{label} quota unavailable");
        StopGhost(ghostMarker, ghostTransform);
        StopUpdateSweep(sweep, sweepTransform);
        row.ToolTip = offline ? "Quota unavailable · live quota source is offline." : "Quota data unavailable.";
    }

    private void TransitionText(TextBlock valueText, TextBlock previousValueText, string value, bool animate)
    {
        if (valueText.Text == value)
        {
            return;
        }

        valueText.BeginAnimation(UIElement.OpacityProperty, null);
        previousValueText.BeginAnimation(UIElement.OpacityProperty, null);
        previousValueText.Visibility = Visibility.Collapsed;
        previousValueText.Opacity = 0;
        var previous = valueText.Text;
        valueText.Text = value;
        valueText.Opacity = 1;
        if (!animate)
        {
            previousValueText.Text = string.Empty;
            return;
        }

        previousValueText.Text = previous;
        previousValueText.Visibility = Visibility.Visible;
        var duration = TimeSpan.FromMilliseconds(125);
        var fadeOut = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            previousValueText.Opacity = 0;
            previousValueText.Visibility = Visibility.Collapsed;
        };
        previousValueText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        valueText.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.05, 1, duration)
            {
                EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void TransitionProgress(
        ProgressBar progress,
        double value,
        bool animate,
        Border? ghostMarker,
        TranslateTransform? ghostTransform,
        bool reset)
    {
        var from = progress.Value;
        progress.BeginAnimation(RangeBase.ValueProperty, null);
        progress.Value = value;
        if (!animate || Math.Abs(from - value) < 0.01)
        {
            if (ghostMarker is not null && ghostTransform is not null)
            {
                StopGhost(ghostMarker, ghostTransform);
            }
            return;
        }

        if (!reset && value < from - 0.05 && ghostMarker is not null && ghostTransform is not null)
        {
            ShowGhost(ghostMarker, ghostTransform, progress, from);
        }
        else if (ghostMarker is not null && ghostTransform is not null)
        {
            StopGhost(ghostMarker, ghostTransform);
        }

        progress.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(reset ? 360 : 200))
            {
                EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void ShowGhost(Border marker, TranslateTransform transform, ProgressBar progress, double oldValue)
    {
        StopGhost(marker, transform);
        transform.X = Math.Clamp(progress.ActualWidth * oldValue / 100 - marker.Width / 2, 0, progress.ActualWidth);
        marker.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0.56, 0, TimeSpan.FromMilliseconds(650))
        {
            BeginTime = TimeSpan.FromMilliseconds(35),
            EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            marker.Opacity = 0;
            marker.Visibility = Visibility.Collapsed;
        };
        marker.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void StopGhost(Border marker, TranslateTransform transform)
    {
        marker.BeginAnimation(UIElement.OpacityProperty, null);
        marker.Opacity = 0;
        marker.Visibility = Visibility.Collapsed;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private void AnimateUpdateSweep(Border sweep, TranslateTransform transform, FrameworkElement row)
    {
        StopUpdateSweep(sweep, transform);
        var distance = row.ActualWidth;
        var width = sweep.Width;
        var duration = TimeSpan.FromMilliseconds(300);
        sweep.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.22, 0, duration)
            {
                EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-width, distance, duration)
            {
                EasingFunction = (IEasingFunction)FindResource("MiniMotionEaseOut"),
                FillBehavior = FillBehavior.Stop
            });
    }

    private static void StopUpdateSweep(Border sweep, TranslateTransform transform)
    {
        sweep.BeginAnimation(UIElement.OpacityProperty, null);
        sweep.Opacity = 0;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = -sweep.Width;
    }

    private static bool IsNewSample(QuotaUsageSnapshot? previous, QuotaUsageSnapshot? current) =>
        previous is not null && current is not null && current.SampledAt > previous.SampledAt;

    private static bool IsNewReset(
        DashboardUsageSnapshot? previousSnapshot,
        DashboardUsageSnapshot currentSnapshot,
        string kind,
        QuotaUsageSnapshot? previousQuota,
        QuotaUsageSnapshot? currentQuota)
    {
        if (previousSnapshot is null || previousQuota is null || currentQuota is null
            || currentQuota.SampledAt <= previousQuota.SampledAt
            || Math.Clamp(100 - previousQuota.UsedPercent, 0, 100) > 20
            || Math.Clamp(100 - currentQuota.UsedPercent, 0, 100) < 90)
        {
            return false;
        }

        return currentSnapshot.QuotaResetEvents.Any(current =>
            string.Equals(current.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && !previousSnapshot.QuotaResetEvents.Any(previous =>
                previous.Kind.Equals(current.Kind, StringComparison.OrdinalIgnoreCase)
                && previous.EventAt == current.EventAt));
    }

    internal static string FormatRemaining(QuotaUsageSnapshot? quota) => quota is null
        ? "—"
        : $"{Math.Clamp(100 - quota.UsedPercent, 0, 100).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)}%";
}
