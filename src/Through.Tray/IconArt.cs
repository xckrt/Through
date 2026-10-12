using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Through.Tray;

/// <summary>
/// Знак Тропы: туристическая метка «бело-цветно-белая», нарисованная краской на тёмной табличке,
/// и пунктир тропы, который её огибает. Рисуется отдельно под каждый размер: на 16 пикселях
/// только табличка и метка по сетке пикселей, на крупных — наклон, неровный край краски и тропа.
/// </summary>
public static class IconArt
{
    public static readonly Color Plate = Color.FromArgb(0x1C, 0x2A, 0x2E);
    public static readonly Color Paint = Color.FromArgb(0xF4, 0xF1, 0xE6);
    public static readonly Color Signal = Color.FromArgb(0xD2, 0x3A, 0x2F);
    public static readonly Color Trail = Color.FromArgb(0x4C, 0xA3, 0x7C);
    public static readonly Color Rim = Color.FromArgb(0x3A, 0x4F, 0x54);

    public static Bitmap Draw(int size, Color stripe)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        using (var plate = RoundRect(new RectangleF(0, 0, s, s), MathF.Max(3, s * 0.22f)))
        using (var fill = new SolidBrush(Plate))
        {
            g.FillPath(fill, plate);
            // тонкий светлый кант: на тёмной панели задач табличка не сливается с фоном
            if (size >= 24)
            {
                var inset = MathF.Max(0.5f, s * 0.012f);
                using var rim = RoundRect(new RectangleF(inset, inset, s - inset * 2, s - inset * 2), MathF.Max(3, s * 0.22f) - inset);
                using var pen = new Pen(Rim, MathF.Max(1, s * 0.016f));
                g.DrawPath(pen, rim);
            }
        }

        if (size >= 40) DrawTrail(g, s);

        if (size <= 24) DrawBlazeCrisp(g, size, stripe);
        else DrawBlazePainted(g, s, stripe, rough: size >= 64);
        return bmp;
    }

    /// <summary>Пунктир тропы: снизу слева, вокруг метки, наверх вправо.</summary>
    private static void DrawTrail(Graphics g, float s)
    {
        // штрихи, как пунктир тропы на туристической карте
        using var pen = new Pen(Trail, s * 0.04f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, DashCap = DashCap.Round,
            DashPattern = [1.6f, 1.5f],
        };
        g.DrawBezier(pen, s * 0.13f, s * 0.90f, s * 0.70f, s * 0.98f, s * 0.95f, s * 0.55f, s * 0.84f, s * 0.12f);
    }

    /// <summary>Маленькие размеры: прямоугольники по пикселям, без наклона — иначе метка размоется.</summary>
    private static void DrawBlazeCrisp(Graphics g, int size, Color stripe)
    {
        g.SmoothingMode = SmoothingMode.None;
        var w = size <= 16 ? 8 : 10;
        var band = size <= 16 ? 4 : 5;
        var x = (size - w) / 2;
        var y = (size - band * 3) / 2;
        using var paint = new SolidBrush(Paint);
        using var mid = new SolidBrush(stripe);
        g.FillRectangle(paint, x, y, w, band);
        g.FillRectangle(mid, x, y + band, w, band);
        g.FillRectangle(paint, x, y + band * 2, w, band);
    }

    /// <summary>Крупные размеры: метка чуть наклонена, как нарисованная на столбе, край краски неровный.</summary>
    private static void DrawBlazePainted(Graphics g, float s, Color stripe, bool rough)
    {
        var w = s * 0.36f;
        var h = s * 0.54f;
        var band = h / 3;
        var state = g.Save();
        g.TranslateTransform(s * 0.46f, s * 0.47f);
        g.RotateTransform(-6);
        g.TranslateTransform(-w / 2, -h / 2);

        var rng = new Random(7); // один и тот же «мазок» на каждой сборке
        using var paint = new SolidBrush(Paint);
        using var mid = new SolidBrush(stripe);
        g.FillPath(paint, Stroke(0, 0, w, band * 1.04f, rough, rng, s));
        g.FillPath(mid, Stroke(0, band, w, band, rough, rng, s));
        g.FillPath(paint, Stroke(0, band * 1.96f, w, band * 1.04f, rough, rng, s));
        g.Restore(state);
    }

    /// <summary>Прямоугольник краски: на крупном размере края слегка гуляют, как от кисти.</summary>
    private static GraphicsPath Stroke(float x, float y, float w, float h, bool rough, Random rng, float s)
    {
        var path = new GraphicsPath();
        if (!rough)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }
        var j = s * 0.006f;
        float J() => (float)(rng.NextDouble() * 2 - 1) * j;
        var pts = new List<PointF>();
        const int steps = 4;
        for (var i = 0; i <= steps; i++) pts.Add(new(x + w * i / steps + J(), y + J()));
        for (var i = 1; i <= 3; i++) pts.Add(new(x + w + J() * 1.6f, y + h * i / 3 + J()));
        for (var i = steps - 1; i >= 0; i--) pts.Add(new(x + w * i / steps + J(), y + h + J()));
        for (var i = 2; i >= 1; i--) pts.Add(new(x + J() * 1.6f, y + h * i / 3 + J()));
        path.AddPolygon(pts.ToArray());
        return path;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Файл .ico из нескольких размеров (PNG внутри, Windows Vista+).</summary>
    public static void SaveIco(string path, Color stripe, params int[] sizes)
    {
        var frames = sizes.Select(size =>
        {
            using var bmp = Draw(size, stripe);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return (size, data: ms.ToArray());
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);
    }
}
