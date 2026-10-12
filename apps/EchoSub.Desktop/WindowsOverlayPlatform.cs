using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace EchoSub.Desktop;

internal static class WindowsOverlayPlatform
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivate = 0x08000000;
    private const long Topmost = 0x00000008;
    private const long ToolWindow = 0x00000080;
    private const long Transparent = 0x00000020;
    private const long Layered = 0x00080000;

    public static void PreventActivation(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) throw new InvalidOperationException("Overlay HWND is unavailable.");
        var style = ReadStyle(handle);
        WriteStyle(handle, style | NoActivate | ToolWindow);
    }

    // Layered + transparent directs mouse input to underlying Windows windows.
    // Restore only the bits owned by this toggle; preserve renderer/activation bits.
    public static void SetClickThrough(Window window, bool enabled, ref long? originalInputBits)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Click-through is implemented for Windows only.");
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) throw new InvalidOperationException("Overlay HWND is unavailable.");
        var style = ReadStyle(handle);
        if (enabled)
        {
            var original = originalInputBits ?? (style & (Transparent | Layered));
            WriteStyle(handle, style | Transparent | Layered);
            originalInputBits = original;
        }
        else if (originalInputBits is { } bits)
        {
            WriteStyle(handle, (style & ~(Transparent | Layered)) | bits);
            originalInputBits = null;
        }
    }

    private static void WriteStyle(nint handle, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtrW(handle, ExtendedStyleIndex, (nint)style)
            : SetWindowLongW(handle, ExtendedStyleIndex, (int)style);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static nint ForegroundWindow() => OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;

    public static NativeWindowState Inspect(Window window)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, false, false, false, "unavailable", "unavailable");
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        var style = ReadStyle(handle);
        return new(handle != 0, IsWindowVisible(handle), (style & Topmost) != 0,
            (style & NoActivate) != 0, (style & ToolWindow) != 0, $"0x{handle:X}", $"0x{style:X}")
            { ClickThrough = (style & (Transparent | Layered)) == (Transparent | Layered) };
    }

    private static long ReadStyle(nint handle) => IntPtr.Size == 8
        ? GetWindowLongPtrW(handle, ExtendedStyleIndex).ToInt64()
        : GetWindowLongW(handle, ExtendedStyleIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
}

internal sealed record NativeWindowState(bool Available, bool Visible, bool Topmost,
    bool NoActivate, bool ToolWindow, string Hwnd, string ExtendedStyle)
{
    public bool ClickThrough { get; init; }
}
