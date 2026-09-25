using System;
using System.Drawing;

namespace Genesis.Runtime.Scene;

/// <summary>World dimensions are not window dimensions. Explicit settings win, then enabled ports,
/// then legacy room size. Optional proportional port scaling preserves pixel-art aspect ratio.</summary>
public static class RoomDisplayLayout
{
    public static Size WindowSize(RoomAsset room)
    {
        if (room == null) return new Size(1280, 720);
        long width = 0, height = 0;
        foreach (RoomViewport port in room.Viewports)
            if (port.Enabled)
            {
                width = Math.Max(width, (long)port.PortX + port.PortWidth);
                height = Math.Max(height, (long)port.PortY + port.PortHeight);
            }
        if (width <= 0) width = room.Settings.Width;
        if (height <= 0) height = room.Settings.Height;
        if (room.Settings.WindowWidth > 0) width = room.Settings.WindowWidth;
        if (room.Settings.WindowHeight > 0) height = room.Settings.WindowHeight;
        return new Size((int)Math.Clamp(width, 1, 16384), (int)Math.Clamp(height, 1, 16384));
    }

    public static Rectangle Port(RoomAsset room, RoomViewport port, int width, int height)
    {
        if (!room.Settings.ScaleViewportsWithWindow)
            return new Rectangle(port.PortX, port.PortY, port.PortWidth, port.PortHeight);
        Size authored = WindowSize(room);
        double scale = Math.Min(Math.Max(1, width) / (double)authored.Width,
            Math.Max(1, height) / (double)authored.Height);
        double left = (width - authored.Width * scale) * .5;
        double top = (height - authored.Height * scale) * .5;
        int x = (int)Math.Round(left + port.PortX * scale);
        int y = (int)Math.Round(top + port.PortY * scale);
        int right = (int)Math.Round(left + ((long)port.PortX + port.PortWidth) * scale);
        int bottom = (int)Math.Round(top + ((long)port.PortY + port.PortHeight) * scale);
        return new Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}
