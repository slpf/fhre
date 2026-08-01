using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace FH6RB.Services;

public sealed class Win32HorizontalOnlyResize
{
    private const int GWLP_WNDPROC = -4;
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x00010000;

    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_GETMINMAXINFO = 0x0024;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _originalProc;
    private readonly WndProc _hook;
    private readonly Func<double>? _contentHeight;

    private Win32HorizontalOnlyResize(IntPtr hwnd, Func<double>? contentHeight)
    {
        _hook = Hook;
        _contentHeight = contentHeight;
        _originalProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & ~WS_MAXIMIZEBOX;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr) style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static Win32HorizontalOnlyResize? TryAttach(Window window, Func<double>? contentHeight = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return hwnd == IntPtr.Zero ? null : new Win32HorizontalOnlyResize(hwnd, contentHeight);
    }

    private IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var result = CallWindowProc(_originalProc, hWnd, msg, wParam, lParam);
            GetWindowRect(hWnd, out var rcWin);
            GetClientRect(hWnd, out var rcClient);
            var full = rcWin.Bottom - rcWin.Top;
            var nonClient = full - (rcClient.Bottom - rcClient.Top);

            var clientPhysical = 0.0;
            try
            {
                clientPhysical = _contentHeight?.Invoke() ?? 0;
            }
            catch
            {
            }

            var h = clientPhysical > 0 ? clientPhysical + nonClient : full;
            if (h > 0)
            {
                var hi = (int) Math.Round(h);
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMaxSize.Y = hi;
                mmi.ptMinTrackSize.Y = hi;
                mmi.ptMaxTrackSize.Y = hi;
                Marshal.StructureToPtr(mmi, lParam, false);
            }

            return result;
        }

        var hit = CallWindowProc(_originalProc, hWnd, msg, wParam, lParam);

        if (msg != WM_NCHITTEST)
        {
            return hit;
        }

        return hit.ToInt32() switch
        {
            HTTOP or HTBOTTOM => (IntPtr)HTCLIENT,
            HTTOPLEFT or HTBOTTOMLEFT => (IntPtr)HTLEFT,
            HTTOPRIGHT or HTBOTTOMRIGHT => (IntPtr)HTRIGHT,
            _ => hit
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);
}
