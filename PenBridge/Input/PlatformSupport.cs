namespace PenBridge.Input;

/// <summary>
/// CreateSyntheticPointerDevice/InjectSyntheticPointerInput require Windows 10 version 1809
/// (build 17763) or later. The POINTER_TYPE_INFO union layout (see NativeMethods) additionally
/// assumes 8-byte pointer alignment, which only holds on 64-bit processes — checked separately
/// via Environment.Is64BitProcess since the .csproj already restricts PlatformTarget to x64.
/// </summary>
public static class PlatformSupport
{
    private static readonly Version MinimumVersion = new(10, 0, 17763);

    public static bool IsSyntheticPointerInputSupported =>
        Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version >= MinimumVersion;
}
