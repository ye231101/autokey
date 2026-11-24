using System.Runtime.InteropServices;
using AutoKey.Core.Execution;
using static AutoKey.Platform.Windows.NativeMethods;

namespace AutoKey.Platform.Windows;

internal sealed class Win32Desktop : IDesktop
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static readonly Lazy<bool> RunningElevated = new(() => IsElevated(GetCurrentProcess()) == true);

    /// <summary>Programs built on the VS Code editor, by the name of their executable.</summary>
    private static readonly HashSet<string> CodeEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "VSCodium - Insiders", "Cursor", "Windsurf", "Trae", "Kiro", "Positron", "Antigravity",
    };

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool IsCodeEditor(nint window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        nint process = processId == 0 ? 0 : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return false;
        }

        try
        {
            var buffer = new char[1024];
            uint length = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref length)
                && CodeEditors.Contains(Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)length)));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public bool IsOwnWindow(nint window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        return processId == OwnProcessId;
    }

    public string GetWindowTitle(nint window)
    {
        int length = GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        int copied = GetWindowTextW(window, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, copied));
    }

    /// <summary>
    /// Windows silently drops simulated input sent to windows of processes with a higher integrity level
    /// (UIPI), typically apps running as administrator.
    /// </summary>
    public bool CanSendInputTo(nint window)
    {
        if (RunningElevated.Value)
        {
            return true;
        }

        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return true;
        }

        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return true; // can't tell; assume it works
        }

        try
        {
            return IsElevated(process) != true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool? IsElevated(nint process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out nint token))
        {
            // Non-elevated processes may not inspect an elevated process's token.
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? true : null;
        }

        try
        {
            return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
