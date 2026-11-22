namespace Starlight.Overlay;

/// <summary>A monitor in physical pixels (virtual-desktop coordinates) with its scale (1.0 = 100 %).</summary>
public sealed record MonitorInfo(int X, int Y, int Width, int Height, double Scale, bool Primary, string Name)
{
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
}

/// <summary>Converts screen positions to the overlay window of one monitor. Plain math, tested in the container.</summary>
public static class OverlayGeometry
{
    /// <summary>A point in physical virtual-desktop pixels → device-independent units inside that monitor's overlay window.</summary>
    public static (double X, double Y) ToLocal(MonitorInfo m, int px, int py) =>
        ((px - m.X) / m.Scale, (py - m.Y) / m.Scale);

    /// <summary>The monitor that holds the point, or the nearest one.</summary>
    public static MonitorInfo MonitorFor(IReadOnlyList<MonitorInfo> monitors, int px, int py) =>
        monitors.FirstOrDefault(m => m.Contains(px, py))
        ?? monitors.OrderBy(m => Distance(m, px, py)).First();

    /// <summary>Where the orb's centre is, in physical pixels: top centre of the primary monitor.</summary>
    public static (int X, int Y) OrbCenter(IReadOnlyList<MonitorInfo> monitors, double orbSizeDip, double marginDip)
    {
        var m = monitors.FirstOrDefault(x => x.Primary) ?? monitors[0];
        return (m.X + m.Width / 2, m.Y + (int)Math.Round((marginDip + orbSizeDip / 2) * m.Scale));
    }

    /// <summary>The piece of a line (physical pixels) that crosses one monitor, clipped to it, in that monitor's local units.
    /// Null when the line does not cross the monitor.</summary>
    public static (double X1, double Y1, double X2, double Y2)? ClipLine(MonitorInfo m, int x1, int y1, int x2, int y2)
    {
        double t0 = 0, t1 = 1;
        double dx = x2 - x1, dy = y2 - y1;
        bool Clip(double p, double q)
        {
            if (p == 0)
            {
                return q >= 0;
            }
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
        if (!Clip(-dx, x1 - m.X) || !Clip(dx, m.X + m.Width - x1) || !Clip(-dy, y1 - m.Y) || !Clip(dy, m.Y + m.Height - y1))
        {
            return null;
        }
        var a = ToLocal(m, (int)Math.Round(x1 + t0 * dx), (int)Math.Round(y1 + t0 * dy));
        var b = ToLocal(m, (int)Math.Round(x1 + t1 * dx), (int)Math.Round(y1 + t1 * dy));
        return (a.X, a.Y, b.X, b.Y);
    }

    private static double Distance(MonitorInfo m, int px, int py)
    {
        var dx = Math.Max(Math.Max(m.X - px, 0), px - (m.X + m.Width));
        var dy = Math.Max(Math.Max(m.Y - py, 0), py - (m.Y + m.Height));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }
}
