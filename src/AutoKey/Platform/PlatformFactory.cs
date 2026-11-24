using AutoKey.Platform.Linux;
using AutoKey.Platform.MacOS;
using AutoKey.Platform.Windows;

namespace AutoKey.Platform;

internal static class PlatformFactory
{
    public static IPlatform Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatform();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacPlatform();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxPlatform();
        }

        throw new PlatformNotSupportedException("AutoKey runs on Windows, macOS and Linux.");
    }
}
