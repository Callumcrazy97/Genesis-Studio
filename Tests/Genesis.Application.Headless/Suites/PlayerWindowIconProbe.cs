using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Genesis.Application.Headless.Suites;

/// <summary>Reads the native icon state from the real out-of-process Player window.</summary>
internal static class PlayerWindowIconProbe
{
    private const uint WmGetIcon = 0x007F;
    private const nuint IconSmall = 0;
    private const nuint IconBig = 1;
    private const nuint IconSmall2 = 2;
    private const uint AbortIfHung = 0x0002;

    public static Result WaitForIcons(Process process, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        Stopwatch timer = Stopwatch.StartNew();
        Result observed = new(false, false, false, 0, 0);

        while (!process.HasExited && timer.Elapsed < timeout)
        {
            observed = Inspect(process.Id);
            if (observed.HasSmallIcon && observed.HasLargeIcon
                && observed.SmallIconWidth > 0 && observed.LargeIconWidth > observed.SmallIconWidth)
                return observed;

            Thread.Sleep(25);
        }

        return observed;
    }

    private static Result Inspect(int processId)
    {
        Result result = new(false, false, false, 0, 0);
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(handle))
                return true;

            nint small = GetIcon(handle, IconSmall);
            if (small == 0)
                small = GetIcon(handle, IconSmall2);

            nint large = GetIcon(handle, IconBig);
            result = new(
                true,
                small != 0,
                large != 0,
                ReadWidth(small),
                ReadWidth(large));
            return !(result.HasSmallIcon && result.HasLargeIcon
                && result.LargeIconWidth > result.SmallIconWidth);
        }, 0);

        return result;
    }

    private static nint GetIcon(nint window, nuint kind)
    {
        nint sent = SendMessageTimeout(
            window,
            WmGetIcon,
            kind,
            0,
            AbortIfHung,
            250,
            out nuint icon);
        return sent == 0 ? 0 : (nint)icon;
    }

    private static int ReadWidth(nint icon)
    {
        if (icon == 0 || !GetIconInfo(icon, out IconInfo info))
            return 0;

        try
        {
            nint bitmap = info.Color != 0 ? info.Color : info.Mask;
            return bitmap != 0
                && GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap data) != 0
                    ? Math.Abs(data.Width)
                    : 0;
        }
        finally
        {
            if (info.Color != 0)
                DeleteObject(info.Color);
            if (info.Mask != 0)
                DeleteObject(info.Mask);
        }
    }

    internal sealed record Result(
        bool FoundWindow,
        bool HasSmallIcon,
        bool HasLargeIcon,
        int SmallIconWidth,
        int LargeIconWidth);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public nint Bits;
    }

    private delegate bool EnumWindow(nint window, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nuint result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint value, int size, out NativeBitmap data);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}
