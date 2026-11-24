namespace AutoKey.Platform.Windows;

/// <summary>
/// Raises the Windows timer resolution to 1 ms while alive. The default 15.6 ms granularity would round
/// short waits (key holds, quick key sequences) up and make the rhythm feel mechanical.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    private bool _active = NativeMethods.timeBeginPeriod(1) == 0;

    public void Dispose()
    {
        if (_active)
        {
            NativeMethods.timeEndPeriod(1);
            _active = false;
        }
    }
}
