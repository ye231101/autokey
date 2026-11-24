using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoKey.Platform.MacOS;

/// <summary>The few macOS system calls AutoKey needs beyond what SharpHook provides.</summary>
[SupportedOSPlatform("macos")]
internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // CGEventSourceStateID: the hardware state only, so keys AutoKey itself presses don't count.
    private const int kCGEventSourceStateHIDSystemState = 1;
    private const ulong kCGEventFlagMaskControl = 0x00040000;
    private const ulong kCGEventFlagMaskAlternate = 0x00080000;
    private const ulong kCGEventFlagMaskCommand = 0x00100000;

    // Declared first: static fields initialise in order, and the Objective-C classes below live in AppKit.
    private static readonly bool AppKitLoaded = NativeLibrary.TryLoad(AppKit, out _);

    private static readonly nint NSWorkspace = objc_getClass("NSWorkspace");
    private static readonly nint NSRunningApplication = objc_getClass("NSRunningApplication");
    private static readonly nint SelSharedWorkspace = sel_registerName("sharedWorkspace");
    private static readonly nint SelFrontmostApplication = sel_registerName("frontmostApplication");
    private static readonly nint SelProcessIdentifier = sel_registerName("processIdentifier");
    private static readonly nint SelRunningApplicationWithPid = sel_registerName("runningApplicationWithProcessIdentifier:");
    private static readonly nint SelLocalizedName = sel_registerName("localizedName");
    private static readonly nint SelUtf8String = sel_registerName("UTF8String");

    /// <summary>Process id of the application that has keyboard focus, or 0.</summary>
    public static int FrontmostProcessId()
    {
        if (!AppKitLoaded || NSWorkspace == 0)
        {
            return 0;
        }

        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint workspace = SendPointer(NSWorkspace, SelSharedWorkspace);
            nint application = workspace == 0 ? 0 : SendPointer(workspace, SelFrontmostApplication);
            return application == 0 ? 0 : SendInt(application, SelProcessIdentifier);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The display name of the application with the given process id.</summary>
    public static string ApplicationName(int processId)
    {
        if (!AppKitLoaded || NSRunningApplication == 0 || processId <= 0)
        {
            return string.Empty;
        }

        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint application = SendPointerInt(NSRunningApplication, SelRunningApplicationWithPid, processId);
            nint name = application == 0 ? 0 : SendPointer(application, SelLocalizedName);
            nint utf8 = name == 0 ? 0 : SendPointer(name, SelUtf8String);
            return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Whether the user is physically holding Control, Option or Command.</summary>
    public static bool UserHoldsModifiers() =>
        (CGEventSourceFlagsState(kCGEventSourceStateHIDSystemState)
            & (kCGEventFlagMaskControl | kCGEventFlagMaskAlternate | kCGEventFlagMaskCommand)) != 0;

    /// <summary>
    /// Secure input (password fields, Terminal's "Secure Keyboard Entry") stops other apps from sending keystrokes.
    /// </summary>
    public static bool IsSecureInputEnabled() => IsSecureEventInputEnabled();

    [DllImport(ObjC)]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName(string name);

    // objc_msgSend must be declared with the exact signature of each method it calls (required on Apple Silicon).
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointerInt(nint receiver, nint selector, int argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern int SendInt(nint receiver, nint selector);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(CoreGraphics)]
    private static extern ulong CGEventSourceFlagsState(int stateId);

    [DllImport(Carbon)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsSecureEventInputEnabled();
}
