using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoKey.Platform.Linux;

/// <summary>Xlib functions (libX11). Window, Atom and KeySym are C <c>unsigned long</c>, hence <c>nuint</c>.</summary>
[SupportedOSPlatform("linux")]
internal static class X11Native
{
    private const string LibX11 = "libX11.so.6";

    public const int Success = 0;
    public const uint XkbUseCoreKbd = 0x0100;
    public const nuint AnyPropertyType = 0;
    public const nuint XA_STRING = 31;

    [DllImport(LibX11)]
    public static extern nint XOpenDisplay(nint displayName);

    [DllImport(LibX11)]
    public static extern int XCloseDisplay(nint display);

    [DllImport(LibX11)]
    public static extern nuint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    public static extern nuint XInternAtom(nint display, string atomName, int onlyIfExists);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(
        nint display,
        nuint window,
        nuint property,
        nint longOffset,
        nint longLength,
        int delete,
        nuint requestedType,
        out nuint actualType,
        out int actualFormat,
        out nuint itemCount,
        out nuint bytesAfter,
        out nint data);

    [DllImport(LibX11)]
    public static extern int XFree(nint data);

    [DllImport(LibX11)]
    public static extern int XGetInputFocus(nint display, out nuint focus, out int revertTo);

    [DllImport(LibX11)]
    public static extern nuint XkbKeycodeToKeysym(nint display, uint keycode, int group, int level);

    /// <summary>Fills an XkbStateRec (18 bytes): byte 0 is the active group, byte 9 the locked modifiers.</summary>
    [DllImport(LibX11)]
    public static extern int XkbGetState(nint display, uint deviceSpec, byte[] state);

    [DllImport(LibX11)]
    public static extern nint XSetErrorHandler(nint handler);
}

/// <summary>
/// Xlib's default error handler exits the process, and errors are normal here (a window can close while it's being
/// queried). Errors on AutoKey's own display connections are ignored; everything else goes to the previous handler.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class X11Errors
{
    private static readonly object Lock = new();
    private static readonly HashSet<nint> OwnDisplays = [];
    private static ErrorHandler? _handler; // kept alive while installed
    private static nint _previous;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ErrorHandler(nint display, nint errorEvent);

    public static void Ignore(nint display)
    {
        lock (Lock)
        {
            OwnDisplays.Add(display);
            if (_handler is null)
            {
                _handler = OnError;
                _previous = X11Native.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_handler));
            }
        }
    }

    public static void Forget(nint display)
    {
        lock (Lock)
        {
            OwnDisplays.Remove(display);
        }
    }

    private static int OnError(nint display, nint errorEvent)
    {
        bool own;
        nint previous;
        lock (Lock)
        {
            own = OwnDisplays.Contains(display);
            previous = _previous;
        }

        if (own || previous == 0)
        {
            return 0;
        }

        return Marshal.GetDelegateForFunctionPointer<ErrorHandler>(previous)(display, errorEvent);
    }
}

/// <summary>Converts X keysyms to characters with libxkbcommon, which ships with practically every Linux desktop.</summary>
[SupportedOSPlatform("linux")]
internal static class XkbCommon
{
    private static volatile bool _unavailable;

    public static uint KeysymToUtf32(uint keysym)
    {
        if (_unavailable)
        {
            return 0;
        }

        try
        {
            return xkb_keysym_to_utf32(keysym);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return 0;
        }
    }

    [DllImport("libxkbcommon.so.0")]
    private static extern uint xkb_keysym_to_utf32(uint keysym);
}
