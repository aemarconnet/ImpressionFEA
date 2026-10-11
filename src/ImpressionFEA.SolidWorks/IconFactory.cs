using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ImpressionFEA.SolidWorks;

/// <summary>
/// Draws the CommandManager icons at every size SOLIDWORKS asks for (20 … 128 px): one horizontal strip per size for
/// the commands (Simulate = logo, Send Update, All Configurations, Job Folder) and the logo alone as the main icon.
/// </summary>
internal static class IconFactory
{
    private static readonly int[] Sizes = [20, 32, 40, 64, 96, 128];

    public static (string[] Strips, string[] Main) Write(string folder)
    {
        Directory.CreateDirectory(folder);
        using var logoStream = typeof(IconFactory).Assembly.GetManifestResourceStream("ImpressionFEA.png")
            ?? throw new InvalidOperationException("Embedded logo missing.");
        using var logo = new Bitmap(logoStream);
        var strips = new string[Sizes.Length];
        var main = new string[Sizes.Length];
        for (int k = 0; k < Sizes.Length; k++)
        {
            int s = Sizes[k];
            strips[k] = Path.Combine(folder, $"commands_{s}.png");
            using (var bmp = new Bitmap(4 * s, s, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                Prepare(g);
                g.DrawImage(logo, new Rectangle(0, 0, s, s));
                DrawUpdate(g, new RectangleF(s, 0, s, s));
                DrawConfigurations(g, new RectangleF(2 * s, 0, s, s));
                DrawFolder(g, new RectangleF(3 * s, 0, s, s));
                bmp.Save(strips[k], ImageFormat.Png);
            }

            main[k] = Path.Combine(folder, $"main_{s}.png");
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                Prepare(g);
                g.DrawImage(logo, new Rectangle(0, 0, s, s));
                bmp.Save(main[k], ImageFormat.Png);
            }
        }

        return (strips, main);
    }

    private static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
    }

    private static readonly Color Blue = Color.FromArgb(0x0B, 0x5C, 0xAD);
    private static readonly Color Orange = Color.FromArgb(0xF0, 0x8A, 0x24);

    /// <summary>Circular arrow: re-export / update.</summary>
    private static void DrawUpdate(Graphics g, RectangleF r)
    {
        float w = r.Width, pad = 0.18f * w, pen = Math.Max(1.6f, 0.11f * w);
        var box = new RectangleF(r.X + pad, r.Y + pad, w - 2 * pad, w - 2 * pad);
        using var p = new Pen(Blue, pen) { StartCap = LineCap.Round };
        g.DrawArc(p, box, -60, 290);
        // Arrow head at the start of the arc (−60°).
        double a = -60 * Math.PI / 180;
        var tip = new PointF(box.X + box.Width / 2 + (float)(box.Width / 2 * Math.Cos(a)), box.Y + box.Height / 2 + (float)(box.Height / 2 * Math.Sin(a)));
        float h = 0.22f * w;
        using var b = new SolidBrush(Blue);
        g.FillPolygon(b, [new PointF(tip.X + 0.15f * h, tip.Y - 0.9f * h), new PointF(tip.X + 0.75f * h, tip.Y + 0.35f * h), new PointF(tip.X - 0.55f * h, tip.Y + 0.2f * h)]);
    }

    /// <summary>Stacked sheets: all configurations.</summary>
    private static void DrawConfigurations(Graphics g, RectangleF r)
    {
        float w = r.Width, sz = 0.52f * w, step = 0.13f * w, pen = Math.Max(1f, 0.06f * w);
        for (int i = 2; i >= 0; i--)
        {
            var rect = new RectangleF(r.X + 0.12f * w + i * step, r.Y + 0.12f * w + (2 - i) * step, sz, sz);
            using var fill = new SolidBrush(i == 0 ? Orange : Color.FromArgb(0xE8, 0xEC, 0xF2));
            using var p = new Pen(Blue, pen);
            g.FillRectangle(fill, rect);
            g.DrawRectangle(p, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    /// <summary>Folder: job folder.</summary>
    private static void DrawFolder(Graphics g, RectangleF r)
    {
        float w = r.Width, x = r.X + 0.12f * w, y = r.Y + 0.24f * w, fw = 0.76f * w, fh = 0.56f * w, pen = Math.Max(1f, 0.06f * w);
        using var back = new SolidBrush(Color.FromArgb(0xE0, 0xA4, 0x2C));
        using var front = new SolidBrush(Color.FromArgb(0xF6, 0xC3, 0x4E));
        using var p = new Pen(Color.FromArgb(0xA8, 0x74, 0x10), pen);
        g.FillPolygon(back, [new PointF(x, y), new PointF(x + 0.35f * fw, y), new PointF(x + 0.45f * fw, y + 0.12f * fh), new PointF(x + fw, y + 0.12f * fh), new PointF(x + fw, y + fh), new PointF(x, y + fh)]);
        g.FillRectangle(front, x, y + 0.28f * fh, fw, 0.72f * fh);
        g.DrawRectangle(p, x, y + 0.28f * fh, fw, 0.72f * fh);
    }
}
