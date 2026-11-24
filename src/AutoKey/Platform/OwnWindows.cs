using Avalonia.Controls;

namespace AutoKey.Platform;

/// <summary>Keeps track of AutoKey's own windows: their native handles and whether one of them is active.</summary>
public sealed class OwnWindows
{
    private readonly object _lock = new();
    private readonly HashSet<nint> _handles = [];
    private readonly HashSet<Window> _active = [];

    /// <summary>Whether one of AutoKey's windows currently has keyboard focus.</summary>
    public bool AnyActive
    {
        get
        {
            lock (_lock)
            {
                return _active.Count > 0;
            }
        }
    }

    public void Track(Window window)
    {
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is { } handle and not 0)
            {
                lock (_lock)
                {
                    _handles.Add(handle);
                }
            }
        };
        window.Activated += (_, _) => SetActive(window, true);
        window.Deactivated += (_, _) => SetActive(window, false);
        window.Closed += (_, _) => SetActive(window, false);
    }

    public bool Contains(nint handle)
    {
        lock (_lock)
        {
            return _handles.Contains(handle);
        }
    }

    private void SetActive(Window window, bool active)
    {
        lock (_lock)
        {
            if (active)
            {
                _active.Add(window);
            }
            else
            {
                _active.Remove(window);
            }
        }
    }
}
