using System.Runtime.Versioning;
using AutoKey.Core.Execution;

namespace AutoKey.Platform.MacOS;

/// <summary>On macOS a "window" is the frontmost application, identified by its process id.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDesktop : IDesktop
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    public nint GetForegroundWindow() => MacNative.FrontmostProcessId();

    public bool IsOwnWindow(nint window) => window == OwnProcessId;

    public string GetWindowTitle(nint window) => MacNative.ApplicationName((int)window);

    public bool CanSendInputTo(nint window) => MacPlatform.HasAccessibilityPermission && !MacNative.IsSecureInputEnabled();
}
