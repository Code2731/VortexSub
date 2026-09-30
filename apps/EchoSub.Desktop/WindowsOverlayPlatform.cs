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

    public static void PreventActivation(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) throw new InvalidOperationException("Overlay HWND is unavailable.");
        var style = ReadStyle(handle);
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtrW(handle, ExtendedStyleIndex, (nint)(style | NoActivate | ToolWindow))
            : SetWindowLongW(handle, ExtendedStyleIndex, (int)(style | NoActivate | ToolWindow));
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
            (style & NoActivate) != 0, (style & ToolWindow) != 0, $"0x{handle:X}", $"0x{style:X}");
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
    bool NoActivate, bool ToolWindow, string Hwnd, string ExtendedStyle);
