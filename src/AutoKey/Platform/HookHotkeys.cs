using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace AutoKey.Platform;

/// <summary>
/// System-wide hotkeys built on SharpHook's global keyboard hook (macOS and Linux). Where the platform allows it
/// (macOS) the hotkey is swallowed so the focused app doesn't receive it. The hook also tracks which modifier keys
/// the user is physically holding.
/// </summary>
internal sealed class HookHotkeys : IGlobalHotkeys
{
    private readonly Binding _toggle;
    private readonly Binding _stop;
    private readonly SimpleGlobalHook _hook = new();
    private readonly bool _canSuppress;
    private readonly object _lock = new();
    private Action? _onToggle;
    private Action? _onStop;
    private bool _started;
    private volatile bool _failed;
    private int _modifiersDown;

    public HookHotkeys(Binding toggle, Binding stop)
    {
        _toggle = toggle;
        _stop = stop;
        _canSuppress = (SupportedFeatures() & UioHookFeature.EventSuppression) != 0;
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
    }

    /// <summary>What SharpHook supports here; none if its native backend can't load (e.g. a missing system library).</summary>
    public static UioHookFeature SupportedFeatures()
    {
        try
        {
            return UioHookProvider.Instance.GetOptionalFeatureSupport();
        }
        catch (Exception)
        {
            return UioHookFeature.None;
        }
    }

    public event Action? Unavailable;

    /// <summary>Whether the user is physically holding Ctrl, Alt (Option) or Meta (Command/Super).</summary>
    public bool UserHoldsModifiers => Volatile.Read(ref _modifiersDown) != 0;

    public string Label(HotkeyAction action) => action == HotkeyAction.ToggleTyping ? _toggle.Label : _stop.Label;

    public bool Register(HotkeyAction action, Action callback)
    {
        lock (_lock)
        {
            if (action == HotkeyAction.ToggleTyping)
            {
                _onToggle = callback;
            }
            else
            {
                _onStop = callback;
            }
        }

        Start();
        return !_failed;
    }

    public void Unregister(HotkeyAction action)
    {
        lock (_lock)
        {
            if (action == HotkeyAction.ToggleTyping)
            {
                _onToggle = null;
            }
            else
            {
                _onStop = null;
            }
        }
    }

    /// <summary>Starts the hook (also used just to track modifier keys).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            _hook.RunAsync(GlobalHookType.Keyboard, true).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        Fail();
                    }
                },
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            Fail();
        }
    }

    public void Dispose()
    {
        try
        {
            _hook.Dispose();
        }
        catch (Exception)
        {
            // Stopping a hook that never started can fail; nothing to clean up then.
        }
    }

    private void Fail()
    {
        _failed = true;
        Unavailable?.Invoke();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
        {
            return; // AutoKey's own typing
        }

        KeyCode key = e.Data.KeyCode;
        int modifier = ModifierBit(key);
        if (modifier != 0)
        {
            Interlocked.Or(ref _modifiersDown, modifier);
            return;
        }

        EventMask mask = e.RawEvent.Mask;
        Action? callback;
        lock (_lock)
        {
            callback = _toggle.Matches(key, mask) ? _onToggle : _stop.Matches(key, mask) ? _onStop : null;
        }

        if (callback is null)
        {
            return;
        }

        if (_canSuppress)
        {
            e.SuppressEvent = true;
        }

        callback(); // callers post to the UI thread, so this returns quickly
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        int modifier = ModifierBit(e.Data.KeyCode);
        if (modifier != 0 && !e.IsEventSimulated)
        {
            Interlocked.And(ref _modifiersDown, ~modifier);
        }
    }

    private static int ModifierBit(KeyCode key) => key switch
    {
        KeyCode.VcLeftControl => 1,
        KeyCode.VcRightControl => 2,
        KeyCode.VcLeftAlt => 4,
        KeyCode.VcRightAlt => 8,
        KeyCode.VcLeftMeta => 16,
        KeyCode.VcRightMeta => 32,
        _ => 0,
    };

    /// <summary>A key plus the modifiers (Ctrl, Alt, Meta) that must be held with it; Shift is ignored.</summary>
    public readonly record struct Binding(KeyCode Key, EventMask Modifiers, string Label)
    {
        public bool Matches(KeyCode key, EventMask mask) =>
            key == Key
            && Has(mask, EventMask.Ctrl) == Has(Modifiers, EventMask.Ctrl)
            && Has(mask, EventMask.Alt) == Has(Modifiers, EventMask.Alt)
            && Has(mask, EventMask.Meta) == Has(Modifiers, EventMask.Meta);

        private static bool Has(EventMask mask, EventMask flags) => (mask & flags) != 0;
    }
}
