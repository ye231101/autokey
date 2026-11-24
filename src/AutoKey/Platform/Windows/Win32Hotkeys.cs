using System.Collections.Concurrent;
using System.Runtime.Versioning;
using static AutoKey.Platform.Windows.NativeMethods;

namespace AutoKey.Platform.Windows;

/// <summary>
/// System-wide hotkeys via <c>RegisterHotKey</c>, owned by a small background thread with its own message loop
/// (independent of the UI framework). Registered keys are consumed, so they don't also reach the focused app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32Hotkeys : IGlobalHotkeys
{
    private const uint WM_INVOKE = WM_APP + 1;

    private readonly Thread _thread;
    private readonly uint _threadId;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<int, Action> _callbacks = []; // used only on the hotkey thread

    public Win32Hotkeys()
    {
        var started = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => MessageLoop(started)) { IsBackground = true, Name = "AutoKey hotkeys" };
        _thread.Start();
        _threadId = started.Task.GetAwaiter().GetResult();
    }

    public event Action? Unavailable
    {
        add { }
        remove { }
    }

    public string Label(HotkeyAction action) => action == HotkeyAction.ToggleTyping ? "F8" : "Esc";

    public bool Register(HotkeyAction action, Action callback) => Invoke(() =>
    {
        int id = Id(action);
        if (_callbacks.ContainsKey(id))
        {
            return true;
        }

        uint key = action == HotkeyAction.ToggleTyping ? VK_F8 : VK_ESCAPE;
        if (!RegisterHotKey(0, id, MOD_NOREPEAT, key))
        {
            return false; // another application already uses this key
        }

        _callbacks[id] = callback;
        return true;
    });

    public void Unregister(HotkeyAction action) => Invoke(() =>
    {
        int id = Id(action);
        if (_callbacks.Remove(id))
        {
            UnregisterHotKey(0, id);
        }

        return true;
    });

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private static int Id(HotkeyAction action) => 0xA001 + (int)action;

    /// <summary>Runs work on the hotkey thread (hotkeys belong to the thread that registers them).</summary>
    private bool Invoke(Func<bool> work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        return PostThreadMessage(_threadId, WM_INVOKE, 0, 0)
            && done.Task.Wait(TimeSpan.FromSeconds(2))
            && done.Task.Result;
    }

    private void MessageLoop(TaskCompletionSource<uint> started)
    {
        // PeekMessage creates this thread's message queue, so PostThreadMessage can reach it.
        PeekMessage(out _, 0, 0, 0, PM_NOREMOVE);
        started.SetResult(GetCurrentThreadId());

        while (GetMessage(out MSG msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && _callbacks.TryGetValue((int)msg.wParam, out Action? callback))
            {
                callback();
            }
            else if (msg.message == WM_INVOKE)
            {
                while (_work.TryDequeue(out Action? work))
                {
                    work();
                }
            }
        }

        foreach (int id in _callbacks.Keys)
        {
            UnregisterHotKey(0, id);
        }
    }
}
