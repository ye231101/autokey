using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AutoKey.Core.Execution;
using static AutoKey.Platform.Linux.X11Native;

namespace AutoKey.Platform.Linux;

/// <summary>Finds the focused window through the window manager's <c>_NET_ACTIVE_WINDOW</c> (EWMH).</summary>
[SupportedOSPlatform("linux")]
internal sealed class X11Desktop(OwnWindows ownWindows) : IDesktop, IDisposable
{
    private static readonly nuint OwnProcessId = (nuint)Environment.ProcessId;

    private readonly object _lock = new();
    private nint _display;
    private bool _initialized;
    private nuint _root;
    private nuint _netActiveWindow;
    private nuint _netWmName;
    private nuint _netWmPid;
    private nuint _utf8String;
    private nuint _wmName;

    public nint GetForegroundWindow()
    {
        lock (_lock)
        {
            if (!Initialize())
            {
                return 0;
            }

            // 0 means nothing is focused, or (under XWayland) a native Wayland window is: either way, don't type.
            if (TryReadLong(_root, _netActiveWindow, out nuint active))
            {
                return (nint)active;
            }

            // Window managers without EWMH support: fall back to the input focus (1 = PointerRoot).
            XGetInputFocus(_display, out nuint focus, out _);
            return focus > 1 ? (nint)focus : 0;
        }
    }

    public bool IsOwnWindow(nint window)
    {
        if (ownWindows.Contains(window))
        {
            return true;
        }

        lock (_lock)
        {
            return Initialize() && TryReadLong((nuint)window, _netWmPid, out nuint processId) && processId == OwnProcessId;
        }
    }

    public string GetWindowTitle(nint window)
    {
        lock (_lock)
        {
            if (!Initialize())
            {
                return string.Empty;
            }

            string title = ReadString((nuint)window, _netWmName, _utf8String, Encoding.UTF8);
            return title.Length > 0 ? title : ReadString((nuint)window, _wmName, XA_STRING, Encoding.Latin1);
        }
    }

    public bool CanSendInputTo(nint window) => true;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_display != 0)
            {
                XCloseDisplay(_display);
                X11Errors.Forget(_display);
                _display = 0;
            }
        }
    }

    private bool Initialize()
    {
        if (_initialized)
        {
            return _display != 0;
        }

        _initialized = true;
        try
        {
            _display = XOpenDisplay(0);
        }
        catch (DllNotFoundException)
        {
            _display = 0;
        }

        if (_display == 0)
        {
            return false;
        }

        X11Errors.Ignore(_display);
        _root = XDefaultRootWindow(_display);
        _netActiveWindow = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
        _netWmName = XInternAtom(_display, "_NET_WM_NAME", 0);
        _netWmPid = XInternAtom(_display, "_NET_WM_PID", 0);
        _utf8String = XInternAtom(_display, "UTF8_STRING", 0);
        _wmName = XInternAtom(_display, "WM_NAME", 0);
        return true;
    }

    /// <summary>Reads the first item of a 32-bit property (which Xlib stores as a C long).</summary>
    private bool TryReadLong(nuint window, nuint property, out nuint value)
    {
        value = 0;
        int status = XGetWindowProperty(
            _display, window, property, 0, 1, 0, AnyPropertyType, out nuint type, out int format, out nuint count, out _, out nint data);
        try
        {
            if (status != Success || type == 0 || data == 0 || format != 32 || count == 0)
            {
                return false;
            }

            value = (nuint)Marshal.ReadIntPtr(data);
            return true;
        }
        finally
        {
            if (data != 0)
            {
                XFree(data);
            }
        }
    }

    private string ReadString(nuint window, nuint property, nuint type, Encoding encoding)
    {
        int status = XGetWindowProperty(
            _display, window, property, 0, 1024, 0, type, out _, out int format, out nuint count, out _, out nint data);
        try
        {
            if (status != Success || data == 0 || format != 8 || count == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[(int)Math.Min(count, 4096)];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return encoding.GetString(bytes);
        }
        finally
        {
            if (data != 0)
            {
                XFree(data);
            }
        }
    }
}

/// <summary>
/// Wayland doesn't reveal which window has focus. The best available signal is whether one of AutoKey's own
/// windows is active: typing waits while AutoKey has focus and pauses if the user switches back to it.
/// </summary>
internal sealed class FocusUnknownDesktop(OwnWindows ownWindows) : IDesktop
{
    private const nint AutoKeyWindow = 1;
    private const nint OtherWindow = 2;

    public nint GetForegroundWindow() => ownWindows.AnyActive ? AutoKeyWindow : OtherWindow;

    public bool IsOwnWindow(nint window) => window == AutoKeyWindow;

    public string GetWindowTitle(nint window) => string.Empty;

    public bool CanSendInputTo(nint window) => true;
}
