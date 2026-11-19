using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Legado.UI3;

sealed class Tray : IDisposable
{
    private const uint SizeMessage = 0x0005, TrayMessage = 0x8001, MouseUp = 0x0202, RightUp = 0x0205;
    private readonly nint handle;
    private readonly AppWindow window;
    private readonly Func<bool> minimizeToTray;
    private readonly Action onExit;
    private readonly WindowCallback callback;
    private readonly nint loadedIcon;
    private NotifyIconData icon;
    private bool installed;

    public Tray(nint handle, AppWindow window, Func<bool> minimizeToTray, Action onExit)
    {
        this.handle = handle;
        this.window = window;
        this.minimizeToTray = minimizeToTray;
        this.onExit = onExit;
        callback = WindowProc;
        installed = SetWindowSubclass(handle, callback, 1, 0);
        loadedIcon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Legado.UI3.ico"), 1,
            GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Handle = handle, Id = 1, Flags = 7, Message = TrayMessage,
            Icon = loadedIcon != 0 ? loadedIcon : LoadIconW(0, 32512), Tip = "Legado·UI3", Info = "", InfoTitle = ""
        };
    }

    public bool Hide()
    {
        if (!installed || !Shell_NotifyIconW(0, ref icon)) return false;
        window.Hide();
        return true;
    }

    private void Show()
    {
        Shell_NotifyIconW(2, ref icon);
        window.Show();
        if (window.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        SetForegroundWindow(handle);
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == SizeMessage && wParam == 1 && minimizeToTray()) Hide();
            if (message == TrayMessage)
            {
                if (lParam == MouseUp) Show();
                else if (lParam == RightUp)
                {
                    var menu = CreatePopupMenu();
                    try
                    {
                        AppendMenuW(menu, 0, 1, "打开Legado·UI3");
                        AppendMenuW(menu, 0, 2, "退出");
                        GetCursorPos(out var point);
                        SetForegroundWindow(handle);
                        var clicked = TrackPopupMenu(menu, 0x0102, point.X, point.Y, 0, handle, 0);
                        if (clicked == 1) Show();
                        else if (clicked == 2) onExit();
                    }
                    finally { DestroyMenu(menu); }
                }
                return 0;
            }
        }
        catch (Exception e) { Program.LogStartup(e); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        Shell_NotifyIconW(2, ref icon);
        if (installed) RemoveWindowSubclass(handle, callback, 1);
        if (loadedIcon != 0) DestroyIcon(loadedIcon);
        installed = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Handle;
        public uint Id, Flags, Message;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowCallback(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, WindowCallback callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, WindowCallback callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint LoadIconW(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
}
