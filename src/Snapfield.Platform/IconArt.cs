using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Snapfield.Platform;

/// <summary>
/// Draws the Snapfield "Stack" mark (two overlapping monitors + cursor on a dark
/// tile) at any size, and packs a multi-resolution .ico. One source of truth for
/// the executable icon, the window icon, and the tray icon.
/// </summary>
public static class IconArt
{
    // 100-unit design space (matches the chosen concept), scaled to the target size.
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var k = size / 100f;

        // Rounded tile with a vertical gradient.
        using (var tile = Rounded(0, 0, 100, 100, 22, k))
        using (var bg = new LinearGradientBrush(new RectangleF(0, 0, size, size),
                   Hex("#1B2140"), Hex("#0B0E1A"), LinearGradientMode.Vertical))
            g.FillPath(bg, tile);

        // Back monitor (amber) then front monitor (blue) — overlap gives depth.
        Monitor(g, k, 24, 30, 40, 30, 5, "#12203F", "#E9A24D", 2.2f);
        Monitor(g, k, 38, 42, 40, 30, 5, "#0E1834", "#8FB0FF", 2.6f);

        // Cursor sitting on the front screen.
        Cursor(g, k, 52, 52, 1.7f, 12f, "#FFFFFF");

        return bmp;
    }

    /// <summary>Writes a multi-image .ico (PNG-encoded frames) for the given sizes.</summary>
    public static void SaveIco(string path, params int[] sizes)
    {
        var frames = sizes.Select(s =>
        {
            using var bmp = Render(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return (size: s, png: ms.ToArray());
        }).ToList();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type = icon
        w.Write((ushort)frames.Count); // image count

        var offset = 6 + frames.Count * 16;
        foreach (var f in frames)
        {
            w.Write((byte)(f.size >= 256 ? 0 : f.size)); // width  (0 = 256)
            w.Write((byte)(f.size >= 256 ? 0 : f.size)); // height
            w.Write((byte)0);          // palette
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // color planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(f.png.Length);     // bytes in resource
            w.Write(offset);           // offset
            offset += f.png.Length;
        }
        foreach (var f in frames) w.Write(f.png);
    }

    // ── drawing helpers ───────────────────────────────────────────────────────
    private static void Monitor(Graphics g, float k, float x, float y, float w, float h, float r,
        string fill, string stroke, float sw)
    {
        using var path = Rounded(x, y, w, h, r, k);
        using var fb = new SolidBrush(Hex(fill));
        using var pen = new Pen(Hex(stroke), sw * k) { LineJoin = LineJoin.Round };
        g.FillPath(fb, path);
        g.DrawPath(pen, path);
    }

    private static void Cursor(Graphics g, float k, float tx, float ty, float scale, float rotDeg, string fill)
    {
        // Classic pointer outline in local units.
        var pts = new[]
        {
            (0f, 0f), (0f, 15f), (4f, 11.5f), (6.6f, 17.5f),
            (9f, 16.5f), (6.4f, 10.6f), (11.4f, 10.6f),
        };
        var rad = rotDeg * Math.PI / 180.0;
        float cos = (float)Math.Cos(rad), sin = (float)Math.Sin(rad);
        var poly = pts.Select(p =>
        {
            var sx = p.Item1 * scale;
            var sy = p.Item2 * scale;
            var rx = sx * cos - sy * sin;
            var ry = sx * sin + sy * cos;
            return new PointF((tx + rx) * k, (ty + ry) * k);
        }).ToArray();

        using var fb = new SolidBrush(Hex(fill));
        using var pen = new Pen(Hex("#20242E"), 0.6f * scale * k) { LineJoin = LineJoin.Round };
        g.FillPolygon(fb, poly);
        g.DrawPolygon(pen, poly);
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r, float k)
    {
        x *= k; y *= k; w *= k; h *= k; var d = r * k * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Hex(string s) => ColorTranslator.FromHtml(s);
}
