using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Starlight.Events;

namespace Starlight.Tools.Desktop;

/// <summary>A screenshot in physical pixels, with where it sits on the virtual desktop.</summary>
public sealed class Screenshot(Bitmap image, ScreenRect area) : IDisposable
{
    public Bitmap Image { get; } = image;
    public ScreenRect Area { get; } = area;

    /// <summary>PNG bytes, shrunk so the longer side is at most <paramref name="maxSide"/> (vision models see ~1 MP at most).</summary>
    public const int PictureMaxSide = 1344;

    /// <summary>Where the picture from <see cref="ToPng"/> sits and its scale, to map x,y back.</summary>
    public PictureFrame Frame(int maxSide = PictureMaxSide) => new(Area, TargetParser.PictureScale(Image.Width, Image.Height, maxSide));

    public byte[] ToPng(int maxSide = PictureMaxSide)
    {
        var scale = TargetParser.PictureScale(Image.Width, Image.Height, maxSide);
        using var ms = new MemoryStream();
        if (scale >= 1)
        {
            Image.Save(ms, ImageFormat.Png);
        }
        else
        {
            using var small = new Bitmap(Math.Max(1, (int)(Image.Width * scale)), Math.Max(1, (int)(Image.Height * scale)));
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Image, 0, 0, small.Width, small.Height);
            }
            small.Save(ms, ImageFormat.Png);
        }
        return ms.ToArray();
    }

    public void Dispose() => Image.Dispose();
}

/// <summary>Screenshots through GDI. The overlay is excluded from capture, so Starlight never sees its own drawing.</summary>
public static partial class ScreenCapture
{
    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>The whole virtual desktop (all monitors).</summary>
    public static ScreenRect VirtualDesktop() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static ScreenRect WindowRect(nint hwnd) =>
        GetWindowRect(hwnd, out var r) ? new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : VirtualDesktop();

    public static Screenshot Capture(ScreenRect area)
    {
        var bmp = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(area.X, area.Y, 0, 0, new Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);
        }
        return new Screenshot(bmp, area);
    }

    /// <summary>A fingerprint of every pixel of an area, to tell whether something changed after an action.
    /// A shrunk copy missed one typed character or a ticked box, and the model then repeated the action.</summary>
    public static long Fingerprint(ScreenRect area)
    {
        using var shot = Capture(area);
        var bmp = shot.Image;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            var hash = unchecked((long)14695981039346656037UL);
            for (var y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                foreach (var b in row)
                {
                    hash = unchecked((hash ^ b) * 1099511628211L);
                }
            }
            return hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
