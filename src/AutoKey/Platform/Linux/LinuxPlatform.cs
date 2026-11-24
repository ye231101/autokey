using System.Runtime.Versioning;
using Avalonia.Controls;
using AutoKey.Core.Execution;
using AutoKey.Core.Keyboard;
using SharpHook.Data;
using SharpHook.Providers;
using SharpHook.Simulation;

namespace AutoKey.Platform.Linux;

[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatform : IPlatform
{
    private readonly SessionMode _mode;
    private readonly OwnWindows _ownWindows = new();
    private readonly TrayHostWatcher _trayHost = new();
    private readonly HookHotkeys _hotkeys;
    private readonly IEventSimulator? _simulator;
    private readonly string? _simulatorError;

    public LinuxPlatform()
    {
        bool wayland = IsWaylandSession();
        _mode = !wayland ? SessionMode.X11
            : CanUseVirtualKeyboard() ? SessionMode.WaylandVirtualKeyboard
            : SessionMode.XWaylandOnly;

        // The backend must be chosen before SharpHook is used for anything else. XRecord/XTest needs no special
        // rights but only reaches X11 apps; the Wayland backend uses uinput, which reaches every app.
        try
        {
            UioHookProvider.Instance.SetLinuxMode(_mode == SessionMode.WaylandVirtualKeyboard ? LinuxMode.Wayland : LinuxMode.XRecord);
        }
        catch (Exception ex)
        {
            _simulatorError = ex.Message; // libuiohook itself couldn't load; reported in the notice
        }

        _hotkeys = new HookHotkeys(
            new HookHotkeys.Binding(KeyCode.VcF8, EventMask.None, "F8"),
            new HookHotkeys.Binding(KeyCode.VcEscape, EventMask.None, "Esc"));
        _hotkeys.Start(); // also tracks the modifier keys the user holds

        Desktop = _mode == SessionMode.WaylandVirtualKeyboard ? new FocusUnknownDesktop(_ownWindows) : new X11Desktop(_ownWindows);

        try
        {
            _simulator = EventSimulator.Create("AutoKey");
        }
        catch (Exception ex)
        {
            _simulatorError = ex.Message;
        }
    }

    private enum SessionMode
    {
        X11,
        XWaylandOnly,
        WaylandVirtualKeyboard,
    }

    public bool SupportsTypingMethods => false;

    public bool IsTrayAvailable => _trayHost.IsAvailable;

    public IDesktop Desktop { get; }

    public IGlobalHotkeys Hotkeys => _hotkeys;

    private static bool CanTypeText => (HookHotkeys.SupportedFeatures() & UioHookFeature.PostText) != 0;

    public IKeyboardDevice CreateKeyboard(TypingMethod method, bool shiftEnterForNewLines)
    {
        if (_simulator is null)
        {
            throw new InvalidOperationException($"AutoKey couldn't set up keyboard input: {_simulatorError}");
        }

        // Characters are typed with the keys of the current layout (read fresh, to pick up layout switches and
        // Caps Lock). On X11, characters the layout lacks are sent as text; if the layout can't be read there,
        // everything is. The Wayland backend can only press keys, so it falls back to US QWERTY.
        bool canTypeText = CanTypeText;
        X11Keymap? keymap = canTypeText ? X11Keymap.TryLoad() : X11Keymap.Load();
        return new HookKeyboardDevice(_simulator, shiftEnterForNewLines, canTypeText, keymap is null ? null : keymap.Find, () => _hotkeys.UserHoldsModifiers);
    }

    public KeyboardGeometry GetKeyboardGeometry() => X11Keymap.Load().Geometry;

    public IDisposable BeginTyping() => NoOpScope.Instance;

    public IReadOnlyCollection<string> FindUntypeableCharacters(string text) =>
        CanTypeText ? [] : X11Keymap.Load().FindMissing(text);

    public PlatformNotice? GetNotice()
    {
        if (_simulatorError is not null)
        {
            return new PlatformNotice("Typing isn't available", $"AutoKey couldn't set up keyboard input: {_simulatorError}");
        }

        return _mode switch
        {
            SessionMode.XWaylandOnly => new PlatformNotice(
                "Limited typing in this Wayland session",
                "AutoKey can only type into apps that run through XWayland. To type into every app, give AutoKey access " +
                "to the virtual keyboard (see the README), or sign in with an X11 session."),
            SessionMode.WaylandVirtualKeyboard => new PlatformNotice(
                "Wayland session",
                "Wayland doesn't tell apps which window has focus, so AutoKey only pauses when you switch back to it. " +
                "It can type the characters on your current keyboard layout."),
            _ => null,
        };
    }

    public string DescribeBlockedTarget(string? targetTitle, string resumeHint) =>
        $"AutoKey can't send keystrokes to this window. Switch to another window and {resumeHint}.";

    public void TrackWindow(Window window) => _ownWindows.Track(window);

    public void MakeNonActivating(Window window)
    {
        // Window managers differ too much; clicking the panel may take focus, and the focus guard pauses typing.
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
        _simulator?.Dispose();
        (Desktop as IDisposable)?.Dispose();
        _trayHost.Dispose();
    }

    private static bool IsWaylandSession()
    {
        string session = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty).Trim().ToLowerInvariant();
        return session switch
        {
            "wayland" => true,
            "x11" => false,
            _ => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
        };
    }

    /// <summary>Whether this user may create a virtual keyboard (write access to /dev/uinput).</summary>
    private static bool CanUseVirtualKeyboard()
    {
        try
        {
            using var device = new FileStream("/dev/uinput", FileMode.Open, FileAccess.Write);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
