using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pucky.App.Services;

/// <summary>
/// A Windows notification-area icon so Pucky can run in the background:
/// click to open the control window; right-click to toggle mapping or quit.
/// Implemented with plain Win32 so the app does not need WinForms/WPF.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIcon(
    UiWindowLauncher windows,
    ControllerService controller,
    IHostApplicationLifetime lifetime,
    ILogger<WindowsTrayIcon> logger) : IHostedService
{
    private const uint WmApp = 0x8000;
    private const uint TrayCallback = WmApp + 1;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmCommand = 0x0111;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint MfString = 0x0;
    private const uint MfChecked = 0x8;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;
    private const int CommandOpen = 1;
    private const int CommandToggle = 2;
    private const int CommandQuit = 3;

    private Thread? _thread;
    private IntPtr _window;
    private IntPtr _icon;
    private uint _taskbarCreated;
    private WndProc? _wndProc;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("PUCKY_NO_TRAY") == "1")
        {
            return Task.CompletedTask;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Pucky tray icon"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_window != IntPtr.Zero)
        {
            PostMessage(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }
        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            _wndProc = WindowProcedure;
            var instance = GetModuleHandle(null);
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = "PuckyTrayWindow"
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                logger.LogDebug("Tray window class registration failed: {Error}", Marshal.GetLastWin32Error());
                return;
            }

            // A hidden top-level window (not message-only) so the context
            // menu can take foreground focus and dismiss normally.
            _window = CreateWindowEx(0, "PuckyTrayWindow", "Pucky", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                return;
            }

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _icon = LoadAppIcon();
            AddIcon();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The Windows tray icon is unavailable");
        }
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayCallback)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WmLButtonUp)
            {
                windows.OpenWindow();
            }
            else if (mouse == WmRButtonUp)
            {
                ShowMenu(window);
            }
            return IntPtr.Zero;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted; the icon must be added again.
            AddIcon();
            return IntPtr.Zero;
        }
        if (message == WmClose)
        {
            DestroyWindow(window);
            return IntPtr.Zero;
        }
        if (message == WmDestroy)
        {
            var data = IconData();
            Shell_NotifyIcon(NimDelete, ref data);
            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu(IntPtr window)
    {
        var menu = CreatePopupMenu();
        try
        {
            var enabled = controller.GetStatus().Enabled;
            AppendMenu(menu, MfString, CommandOpen, "Open Pucky");
            AppendMenu(menu, MfString | (enabled ? MfChecked : 0), CommandToggle, "Mapping enabled");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CommandQuit, "Quit Pucky");

            GetCursorPos(out var point);
            SetForegroundWindow(window);
            var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, window, IntPtr.Zero);
            PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case CommandOpen:
                    windows.OpenWindow();
                    break;
                case CommandToggle:
                    if (enabled) controller.Disable(); else controller.Enable();
                    break;
                case CommandQuit:
                    lifetime.StopApplication();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        var data = IconData();
        data.Flags = NifMessage | NifIcon | NifTip;
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        CallbackMessage = TrayCallback,
        Icon = _icon,
        Tip = "Pucky - Steam Controller mapper"
    };

    private static IntPtr LoadAppIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            ExtractIconEx(path, 0, IntPtr.Zero, out var small, 1) > 0 &&
            small != IntPtr.Zero)
        {
            return small;
        }

        var icoPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
        if (File.Exists(icoPath))
        {
            var loaded = LoadImage(IntPtr.Zero, icoPath, 1, 16, 16, 0x10);
            if (loaded != IntPtr.Zero)
            {
                return loaded;
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int index, IntPtr large, out IntPtr small, uint icons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
