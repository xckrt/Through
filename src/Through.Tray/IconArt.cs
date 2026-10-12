using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Through.Tray;

/// <summary>
/// Знак «Сквозь»: буква-портал. На индиго-табличке белое кольцо (проём), сквозь которое справа проходит
/// искра с коротким следом — метафора имени. Цвет искры = состояние (мята — работает, коралл — беда).
/// Рисуется под каждый размер: на 16 пикселях — только кольцо и точка, крупнее — свечение и след.
/// </summary>
public static class IconArt
{
    public static readonly Color PlateTop = Color.FromArgb(0x5A, 0x47, 0xFF);
    public static readonly Color PlateBottom = Color.FromArgb(0x3A, 0x28, 0xD6);
    public static readonly Color Ring = Color.FromArgb(0xF4, 0xF2, 0xFF);
    public static readonly Color Rim = Color.FromArgb(0x6E, 0x5C, 0xF0);
    // цвет искры по умолчанию — мята «сквозь»
    public static readonly Color Spark = Color.FromArgb(0x2B, 0xE3, 0xA6);

    public static Bitmap Draw(int size, Color spark)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size;

        // табличка: индиго-градиент
        var radius = MathF.Max(3, s * 0.24f);
        using (var plate = RoundRect(new RectangleF(0, 0, s, s), radius))
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, s, s), PlateTop, PlateBottom, LinearGradientMode.Vertical))
        {
            g.FillPath(fill, plate);
            if (size >= 24)
            {
                var inset = MathF.Max(0.5f, s * 0.012f);
                using var rim = RoundRect(new RectangleF(inset, inset, s - inset * 2, s - inset * 2), radius - inset);
                using var pen = new Pen(Rim, MathF.Max(1, s * 0.016f));
                g.DrawPath(pen, rim);
            }
        }

        // проём (кольцо) слева от центра
        float cx = s * 0.42f, cy = s * 0.5f, rO = s * 0.27f;
        float thick = MathF.Max(2, s * 0.11f);
        using (var ringPen = new Pen(Ring, thick))
            g.DrawEllipse(ringPen, cx - rO, cy - rO, rO * 2, rO * 2);

        // искра, проходящая сквозь проём, с коротким следом и свечением
        float sparkX = s * 0.64f, sparkR = MathF.Max(1.5f, s * 0.085f);
        if (size >= 40)
        {
            // свечение
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(sparkX - sparkR * 2.4f, cy - sparkR * 2.4f, sparkR * 4.8f, sparkR * 4.8f);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(150, spark),
                SurroundColors = [Color.FromArgb(0, spark)],
                CenterPoint = new PointF(sparkX, cy),
            };
            g.FillPath(glow, glowPath);
            // след: две затухающие точки через проём
            using var t1 = new SolidBrush(Color.FromArgb(70, spark));
            using var t2 = new SolidBrush(Color.FromArgb(130, spark));
            g.FillEllipse(t1, cx - sparkR * 0.5f, cy - sparkR * 0.5f, sparkR, sparkR);
            g.FillEllipse(t2, s * 0.53f - sparkR * 0.6f, cy - sparkR * 0.6f, sparkR * 1.2f, sparkR * 1.2f);
        }
        using (var core = new SolidBrush(spark))
            g.FillEllipse(core, sparkX - sparkR, cy - sparkR, sparkR * 2, sparkR * 2);
        if (size >= 32)
            using (var hi = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                g.FillEllipse(hi, sparkX - sparkR * 0.35f, cy - sparkR * 0.55f, sparkR * 0.8f, sparkR * 0.8f);

        return bmp;
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
    public static void SaveIco(string path, Color spark, params int[] sizes)
    {
        var frames = sizes.Select(size =>
        {
            using var bmp = Draw(size, spark);
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
