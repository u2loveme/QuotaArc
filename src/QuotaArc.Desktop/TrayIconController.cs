using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace QuotaArc.Desktop;

internal sealed class TrayIconController : IDisposable
{
    private const int TrayCallbackMessage = 0x8001;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const uint WmGetIcon = 0x007F;
    private static readonly IntPtr IconSmall2 = new(2);
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _source;
    private readonly ContextMenu _menu;
    private readonly Action _showMain;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconController(Window window, Action showMain, Action showCompact, Action exit)
    {
        ArgumentNullException.ThrowIfNull(window);
        _showMain = showMain;
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        _menu.PlacementTarget = window;
        _menu.Items.Add(CreateMenuItem("Open QuotaArc", (_, _) => _showMain()));
        _menu.Items.Add(CreateMenuItem("Compact view", (_, _) => showCompact()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CreateMenuItem("Exit", (_, _) => exit()));

        if (_source is not null)
        {
            _source.AddHook(WindowMessageHook);
            var data = CreateIconData(_windowHandle);
            _iconAdded = Shell_NotifyIcon(NimAdd, ref data);
            if (_iconAdded)
            {
                data.TimeoutOrVersion = 4;
                _ = Shell_NotifyIcon(NimSetVersion, ref data);
            }
        }
    }

    public bool IsAvailable => _iconAdded;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _menu.IsOpen = false;
        _menu.Items.Clear();
        if (_iconAdded)
        {
            var data = CreateIconData(_windowHandle);
            _ = Shell_NotifyIcon(NimDelete, ref data);
            _iconAdded = false;
        }
        _source?.RemoveHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != TrayCallbackMessage)
        {
            return IntPtr.Zero;
        }

        var action = unchecked((ushort)lParam.ToInt64());
        if (action is WmLeftButtonUp or WmLeftButtonDoubleClick)
        {
            _showMain();
            handled = true;
        }
        else if (action == WmRightButtonUp)
        {
            _menu.IsOpen = true;
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static MenuItem CreateMenuItem(string label, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = label };
        item.Click += onClick;
        return item;
    }

    private static NotifyIconData CreateIconData(IntPtr windowHandle)
    {
        var iconHandle = SendMessage(windowHandle, WmGetIcon, IconSmall2, IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(0), IntPtr.Zero);
        }
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        return new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = windowHandle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = iconHandle,
            Tooltip = "QuotaArc",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
    }
}
