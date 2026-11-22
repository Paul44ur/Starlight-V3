using System.Drawing.Imaging;
using Starlight.Events;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Starlight.Tools.Desktop;

public sealed record OcrLine(string Text, ScreenRect Box);

/// <summary>Reads text on screen with Windows' own OCR (no extra install).</summary>
public static class Ocr
{
    public static async Task<List<OcrLine>> ReadAsync(Screenshot shot, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new ToolException("Windows OCR is not available. Add the English language pack in Windows settings.");
        using var ms = new MemoryStream();
        shot.Image.Save(ms, ImageFormat.Bmp);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(ms.ToArray().AsBuffer()).AsTask(ct);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            double l = double.MaxValue, t = double.MaxValue, r = 0, b = 0;
            foreach (var w in line.Words)
            {
                l = Math.Min(l, w.BoundingRect.X);
                t = Math.Min(t, w.BoundingRect.Y);
                r = Math.Max(r, w.BoundingRect.X + w.BoundingRect.Width);
                b = Math.Max(b, w.BoundingRect.Y + w.BoundingRect.Height);
            }
            if (line.Words.Count > 0)
            {
                lines.Add(new OcrLine(line.Text, new ScreenRect(shot.Area.X + (int)l, shot.Area.Y + (int)t, (int)(r - l), (int)(b - t))));
            }
        }
        return lines;
    }
}
