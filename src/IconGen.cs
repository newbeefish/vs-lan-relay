using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static void Main()
    {
        string dir = @"C:\Users\Administrator\Documents\deepseek-harness\default-workspace\VsRelay";
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

        using (Bitmap b = Render(256))
            b.Save(Path.Combine(dir, "icon_preview.png"), ImageFormat.Png);

        var blobs = new System.Collections.Generic.List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap b = Render(s))
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                blobs.Add(ms.ToArray());
            }
        }

        using (FileStream fs = new FileStream(Path.Combine(dir, "app.ico"), FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)blobs[i].Length);
                w.Write((uint)offset);
                offset += blobs[i].Length;
            }
            foreach (byte[] b in blobs) w.Write(b);
        }
        Console.WriteLine("生成完成");
    }

    static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float cx = size / 2f, cy = size / 2f;
            float R = size * 0.49f;

            // ===== 黄六边形 =====
            PointF[] hex = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                double a = Math.PI / 180.0 * (60 * i - 90);
                hex[i] = new PointF((float)(cx + R * Math.Cos(a)), (float)(cy + R * Math.Sin(a)));
            }
            using (GraphicsPath hp2 = new GraphicsPath())
            {
                hp2.AddPolygon(hex);
                using (LinearGradientBrush lb = new LinearGradientBrush(
                    new PointF(cx - R, cy - R), new PointF(cx + R, cy + R),
                    Color.FromArgb(255, 232, 84), Color.FromArgb(244, 178, 22)))
                    g.FillPath(lb, hp2);
                using (Pen p = new Pen(Color.FromArgb(188, 126, 8), Math.Max(1f, size * 0.022f)))
                    g.DrawPath(p, hp2);
            }

            // ===== 扳手（保持原来的居中大扳手）=====
            Color wrenchCol = Color.FromArgb(46, 46, 52);
            float oh = size * 0.150f;
            float ih = size * 0.072f;
            PointF headPt = new PointF(cx, cy - size * 0.130f);
            RectangleF ro = new RectangleF(headPt.X - oh, headPt.Y - oh, oh * 2, oh * 2);
            RectangleF ri = new RectangleF(headPt.X - ih, headPt.Y - ih, ih * 2, ih * 2);
            using (GraphicsPath jp = new GraphicsPath())
            {
                jp.AddArc(ro, -62, 304);
                jp.AddArc(ri, 242, -304);
                jp.CloseFigure();
                using (SolidBrush wb = new SolidBrush(wrenchCol)) g.FillPath(wb, jp);
            }
            float hw = Math.Max(1.6f, size * 0.088f);
            using (Pen pen = new Pen(wrenchCol, hw))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, headPt.X, headPt.Y + ih * 0.55f, headPt.X, cy + size * 0.185f);
            }

            // ===== 文字：纯白、大字、居中，盖在扳手上 =====
            if (size >= 32)
            {
                string txt = "newbeefish";
                FontFamily fam = new FontFamily("Arial");
                float targetW = size * 0.955f;
                float fs = size * 0.30f;
                for (int i = 0; i < 8; i++)
                {
                    GraphicsPath test = new GraphicsPath();
                    test.AddString(txt, fam, (int)FontStyle.Bold, fs, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF bb = test.GetBounds();
                    test.Dispose();
                    if (bb.Width < 1f) break;
                    float k = targetW / bb.Width;
                    if (k > 0.995f && k < 1.005f) break;
                    fs *= k;
                }

                GraphicsPath tp = new GraphicsPath();
                tp.AddString(txt, fam, (int)FontStyle.Bold, fs, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF tb = tp.GetBounds();
                Matrix m = new Matrix();
                m.Translate(cx - tb.Width / 2f - tb.X, cy + size * 0.015f - tb.Height / 2f - tb.Y);
                tp.Transform(m);

                // 极细的深色轮廓，只为在黄底上勾出字形，视觉上仍是白字
                using (Pen edge = new Pen(Color.FromArgb(120, 78, 6), Math.Max(1.0f, size * 0.0115f)))
                {
                    edge.LineJoin = LineJoin.Round;
                    g.DrawPath(edge, tp);
                }
                using (SolidBrush fb = new SolidBrush(Color.White))
                    g.FillPath(fb, tp);

                tp.Dispose();
                fam.Dispose();
            }
        }
        return bmp;
    }
}
