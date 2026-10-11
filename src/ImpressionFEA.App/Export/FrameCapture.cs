using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImpressionFEA.App.Export;

/// <summary>Renders live WPF elements (viewports, chart, panels) to bitmaps for images, videos and reports.</summary>
public static class FrameCapture
{
    /// <summary>Renders one or more elements side by side (left to right) on a white background at <paramref name="scale"/>.</summary>
    public static BitmapSource Render(double scale, params FrameworkElement[] elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        double w = elements.Sum(e => e.ActualWidth), h = elements.Max(e => e.ActualHeight);
        if (w < 2 || h < 2) throw new InvalidOperationException("Nothing to capture: the view is not visible.");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            double x = 0;
            foreach (var e in elements)
            {
                dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(x, 0, e.ActualWidth, e.ActualHeight));
                x += e.ActualWidth;
            }
        }

        int pw = Even((int)Math.Round(w * scale)), ph = Even((int)Math.Round(h * scale));
        var bmp = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Renders a single element (through a VisualBrush, so its position in the layout does not offset the image).</summary>
    public static BitmapSource RenderElement(FrameworkElement e, double scale) => Render(scale, e);

    private static int Even(int v) => Math.Max(2, v & ~1);

    /// <summary>Opaque BGRA bytes of a frame, padded / cropped to exactly <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static byte[] Bgra(BitmapSource bmp, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        var src = bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
        int w = Math.Min(width, src.PixelWidth), h = Math.Min(height, src.PixelHeight);
        var buf = new byte[width * height * 4];
        var row = new byte[src.PixelWidth * 4];
        for (int y = 0; y < h; y++)
        {
            src.CopyPixels(new Int32Rect(0, y, src.PixelWidth, 1), row, row.Length, 0);
            Buffer.BlockCopy(row, 0, buf, y * width * 4, w * 4);
        }

        for (int i = 3; i < buf.Length; i += 4) buf[i] = 255; // opaque (background is white already)
        return buf;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static string PngBase64(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
