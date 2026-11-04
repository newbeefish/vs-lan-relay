using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 逐像素透明叠加层（无紫边），实时跟随游戏窗口
public class OverlayForm : Form
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT ppsrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte Op; public byte Flags; public byte Alpha; public byte Format; }

    const int ULW_ALPHA = 2;
    const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    IntPtr gameHwnd = IntPtr.Zero;
    Rectangle content;
    int lastW = -1, lastH = -1, lastX = -1, lastY = -1;

    public string Id1P = "";
    public string Id2P = "";
    public bool IAm1P = false;
    public bool ShowIds = true;
    public string RttText = "";        // 右下角显示的延迟

    static readonly Bitmap[] emojiImgs = new Bitmap[6];
    static bool emojiTried = false;

    static void LoadEmoji()
    {
        if (emojiTried) return;
        emojiTried = true;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            for (int i = 1; i <= 6; i++)
            {
                foreach (string n in names)
                {
                    if (n.EndsWith("emoji" + i + ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        using (System.IO.Stream s = asm.GetManifestResourceStream(n))
                            emojiImgs[i - 1] = new Bitmap(s);
                        break;
                    }
                }
            }
        }
        catch { }
    }

    static Bitmap bubbleP1 = null, bubbleP2 = null;

    static Bitmap GetBubble(bool isP1)
    {
        if (isP1 && bubbleP1 != null) return bubbleP1;
        if (!isP1 && bubbleP2 != null) return bubbleP2;
        Bitmap src = BubbleImg();
        if (src == null) return null;
        try
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.Clear(Color.Transparent);
                System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix();
                m.Translate(src.Width / 2f, src.Height / 2f);
                m.Rotate(180f);
                if (!isP1) m.Scale(-1f, 1f);
                gg.Transform = m;
                gg.DrawImage(src, new Rectangle(-src.Width / 2, -src.Height / 2, src.Width, src.Height));
            }
            if (isP1) bubbleP1 = b; else bubbleP2 = b;
            return b;
        }
        catch { return src; }
    }

    static Bitmap bubbleImg = null;
    static bool bubbleTried = false;

    static Bitmap BubbleImg()
    {
        if (bubbleTried) return bubbleImg;
        bubbleTried = true;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith("bubble.png", StringComparison.OrdinalIgnoreCase))
                {
                    using (System.IO.Stream s = asm.GetManifestResourceStream(n))
                        bubbleImg = new Bitmap(s);
                    break;
                }
            }
        }
        catch { }
        return bubbleImg;
    }

    class Bubble { public bool IsP1; public int Emoji; public DateTime Born; }
    readonly Bubble[] bubbles = new Bubble[2];

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 60;
        t.Tick += delegate { Tick(); };
        t.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000020;
            cp.ExStyle |= 0x00080000;
            cp.ExStyle |= 0x00000080;
            cp.ExStyle |= 0x08000000;
            return cp;
        }
    }

    public void AttachTo(IntPtr hwnd) { gameHwnd = hwnd; }

    public void Emoji(bool isP1, int idx)
    {
        bubbles[isP1 ? 0 : 1] = new Bubble { IsP1 = isP1, Emoji = idx, Born = DateTime.Now };
    }

    void Tick()
    {
        if (!Enabled) { if (Visible) Hide(); return; }

        if (gameHwnd == IntPtr.Zero || !IsWindowVisible(gameHwnd))
        {
            gameHwnd = FindGameWindow();
            if (gameHwnd == IntPtr.Zero) { Hide(); return; }
        }

        if (GetForegroundWindow() != gameHwnd) { if (Visible) Hide(); return; }

        RECT r;
        if (!GetClientRect(gameHwnd, out r)) { Hide(); return; }
        POINT p = new POINT();
        ClientToScreen(gameHwnd, ref p);
        int w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0) { Hide(); return; }

        // 4:3 内容区，始终居中（F11 放大后也准确）
        content = Content43(p.X, p.Y, w, h);
        lastW = w; lastH = h; lastX = p.X; lastY = p.Y;

        for (int i = 0; i < 2; i++)
            if (bubbles[i] != null && (DateTime.Now - bubbles[i].Born).TotalMilliseconds > 1600) bubbles[i] = null;

        Render(p.X, p.Y, w, h);
        if (!Visible) Show();
    }

    void Render(int x, int y, int w, int h)
    {
        try
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                int cx = content.X - x, cy = content.Y - y;
                int cw = content.Width, ch = content.Height;

                if (ShowIds)
                {
                    // 1P 在左、2P 在右，位置贴近中间上方的血条下方
                    // 贴窗口顶部；1P 在左侧头像右边，2P 在右侧头像左边
                    int idY = cy + (int)(ch * 0.004);
                    if (idY < cy) idY = cy;    // 不能超出画面顶部
                    DrawId(g, cx + (int)(cw * 0.125), idY, Id1P, true);
                    DrawId(g, cx + (int)(cw * 0.875), idY, Id2P, false);
                }
                for (int i = 0; i < 2; i++)
                    if (bubbles[i] != null) DrawBubble(g, i == 0, bubbles[i], cx, cy, cw, ch);

                PushToWindow(bmp, x, y, w, h);
            }
        }
        catch { }
    }

    void PushToWindow(Bitmap bmp, int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            SIZE sz = new SIZE { CX = w, CY = h };
            POINT src = new POINT { X = 0, Y = 0 };
            POINT dst = new POINT { X = x, Y = y };
            BLENDFUNCTION bf = new BLENDFUNCTION { Op = AC_SRC_OVER, Flags = 0, Alpha = 255, Format = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bf, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // 按 4:3 计算居中内容区
    static Rectangle Content43(int sx, int sy, int w, int h)
    {
        const double Ratio = 4.0 / 3.0;
        double cur = (double)w / h;
        int cw, ch;
        if (cur > Ratio) { ch = h; cw = (int)Math.Round(h * Ratio); }
        else { cw = w; ch = (int)Math.Round(w / Ratio); }
        int ox = sx + (w - cw) / 2;
        int oy = sy + (h - ch) / 2;
        return new Rectangle(ox, oy, cw, ch);
    }

    public bool Enabled = true;

    static Rectangle DetectContent(int sx, int sy, int w, int h)
    {
        try
        {
            int sw = Math.Min(w, 480), sh = Math.Min(h, 480);
            using (Bitmap bmp = new Bitmap(sw, sh))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(sx, sy, 0, 0, new Size(sw, sh));
                int left = 0, right = sw - 1, top = 0, bottom = sh - 1;
                int stepY = Math.Max(1, sh / 30), stepX = Math.Max(1, sw / 30);
                while (left < right && ColDark(bmp, left, 0, sh, stepY)) left++;
                while (right > left && ColDark(bmp, right, 0, sh, stepY)) right--;
                while (top < bottom && RowDark(bmp, top, 0, sw, stepX)) top++;
                while (bottom > top && RowDark(bmp, bottom, 0, sw, stepX)) bottom--;
                int cw = right - left + 1, ch = bottom - top + 1;
                if (cw < w / 2 || ch < h / 2) return new Rectangle(sx, sy, w, h);
                double kx = (double)w / sw, ky = (double)h / sh;
                return new Rectangle(sx + (int)(left * kx), sy + (int)(top * ky), (int)(cw * kx), (int)(ch * ky));
            }
        }
        catch { return new Rectangle(sx, sy, w, h); }
    }

    static bool ColDark(Bitmap b, int x, int y0, int y1, int step)
    {
        int d = 0, t = 0;
        for (int y = y0; y < y1; y += step) { Color c = b.GetPixel(x, y); if (c.R < 18 && c.G < 18 && c.B < 18) d++; t++; }
        return t > 0 && d > t * 0.92;
    }

    static bool RowDark(Bitmap b, int y, int x0, int x1, int step)
    {
        int d = 0, t = 0;
        for (int x = x0; x < x1; x += step) { Color c = b.GetPixel(x, y); if (c.R < 18 && c.G < 18 && c.B < 18) d++; t++; }
        return t > 0 && d > t * 0.92;
    }

    static IntPtr FindGameWindow()
    {
        try
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string n = p.ProcessName;
                    string t = p.MainWindowTitle;
                    if (n.StartsWith("VsRelay", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t != null && t.IndexOf("联机助手", StringComparison.Ordinal) >= 0) continue;
                    if (n.IndexOf("Bleach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Naruto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (t != null && (t.IndexOf("死神", StringComparison.Ordinal) >= 0 || t.IndexOf("火影", StringComparison.Ordinal) >= 0)))
                        return p.MainWindowHandle;
                }
                catch { }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    void DrawRtt(Graphics g, int cx, int cy, int cw, int ch)
    {
        using (Font f = new Font("Microsoft YaHei", 10F, FontStyle.Regular))
        {
            SizeF sz = g.MeasureString(RttText, f);
            int padX = 8, padY = 4;
            int bw = (int)sz.Width + padX * 2;
            int bh = (int)sz.Height + padY * 2;
            int bx = cx + cw - bw - (int)(cw * 0.012);      // 右下角
            int by = cy + ch - bh - (int)(ch * 0.012);

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            using (GraphicsPath gp = RoundRect(new Rectangle(bx, by, bw, bh), 5))
                g.FillPath(bg, gp);

            using (SolidBrush br = new SolidBrush(Color.White))
                g.DrawString(RttText, f, br, bx + padX, by + padY);
        }
    }

    static void DrawImg(Graphics g, Bitmap im, Rectangle dst, System.Drawing.Imaging.ImageAttributes ia, bool mirror)
    {
        if (!mirror)
        {
            if (ia != null) g.DrawImage(im, dst, 0, 0, im.Width, im.Height, GraphicsUnit.Pixel, ia);
            else g.DrawImage(im, dst, 0, 0, im.Width, im.Height, GraphicsUnit.Pixel);
            return;
        }
        System.Drawing.Drawing2D.Matrix old = g.Transform;
        System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix();
        m.Translate(dst.X + dst.Width, dst.Y);
        m.Scale(-1f, 1f);
        g.Transform = m;
        Rectangle flipped = new Rectangle(0, 0, dst.Width, dst.Height);
        if (ia != null) g.DrawImage(im, flipped, 0, 0, im.Width, im.Height, GraphicsUnit.Pixel, ia);
        else g.DrawImage(im, flipped, 0, 0, im.Width, im.Height, GraphicsUnit.Pixel);
        g.Transform = old;
    }

    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void DrawId(Graphics g, int x, int y, string text, bool leftSide)
    {
        if (string.IsNullOrEmpty(text)) return;
        using (Font f = new Font("Microsoft YaHei", 12F, FontStyle.Regular))
        {
            SizeF sz = g.MeasureString(text, f);
            int padX = 12, padY = 5;
            int bw = (int)sz.Width + padX * 2;
            int bh = (int)sz.Height + padY * 2;
            int bx = leftSide ? x : x - bw;
            Rectangle box = new Rectangle(bx, y, bw, bh);
            using (GraphicsPath gp = RoundRect(box, 8))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(235, 222, 184, 135)))
                    g.FillPath(fill, gp);
                using (Pen pen = new Pen(Color.FromArgb(255, 120, 80, 40), 4))
                    g.DrawPath(pen, gp);
            }
            using (SolidBrush br = new SolidBrush(Color.Black))
                g.DrawString(text, f, br, bx + padX, y + padY);
        }
    }

    void DrawBubble(Graphics g, bool isP1, Bubble b, int cx, int cy, int cw, int ch)
    {
        double age = (DateTime.Now - b.Born).TotalMilliseconds;
        int alpha = age > 1300 ? (int)(255 * (1600 - age) / 300) : 255;
        if (alpha < 0) alpha = 0;
        if (alpha > 255) alpha = 255;

        Bitmap img = GetBubble(isP1);
        int bw = Math.Max(92, (int)(ch * 0.20));
        int bh = (int)(bw * 1.14);
        int bx = isP1 ? cx + (int)(cw * 0.015) : cx + (int)(cw * 0.985) - bw;
        int by = cy + (int)(ch * 0.075);

        // 形变：从扁（0.08 高度）撑到正常，顶边不动，向下展开
        double t = age / 240.0;
        if (t > 1) t = 1;
        double ease = 1 - Math.Pow(1 - t, 3);
        double sy = 0.08 + 0.92 * ease;

        if (img != null)
        {
            int h2 = (int)(bh * sy);
            if (h2 < 3) h2 = 3;
            Rectangle dstR = new Rectangle(bx, by, bw, h2);
            System.Drawing.Imaging.ImageAttributes ia = null;
            try
            {
                if (alpha < 255)
                {
                    System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                    cm.Matrix33 = alpha / 255f;
                    ia = new System.Drawing.Imaging.ImageAttributes();
                    ia.SetColorMatrix(cm);
                }
                if (ia != null)
                    g.DrawImage(img, dstR, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                else
                    g.DrawImage(img, dstR, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            }
            finally { if (ia != null) ia.Dispose(); }
        }

        int es = (int)(bw * 0.50);
        int eh = (int)(es * 0.82 * sy);
        if (eh < 2) eh = 2;
        int eTop = by + (int)(bh * sy * 0.40);
        DrawEmoji(g, new Rectangle(bx + (bw - es) / 2, eTop, es, eh), b.Emoji, alpha, !isP1);
    }

    void DrawEmoji(Graphics g, Rectangle r, int idx, int alpha)
    { DrawEmoji(g, r, idx, alpha, false); }

    void DrawEmoji(Graphics g, Rectangle r, int idx, int alpha, bool mirror)
    {
        LoadEmoji();
        Bitmap im = emojiImgs[idx % 6];
        if (im != null)
        {
            try
            {
                if (alpha >= 255 && !mirror) { g.DrawImage(im, r); return; }
                System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes();
                try
                {
                    System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                    cm.Matrix33 = alpha / 255f;
                    ia.SetColorMatrix(cm);
                    DrawImg(g, im, r, ia, mirror);
                }
                finally { ia.Dispose(); }
                return;
            }
            catch { }
        }

        int w = r.Width, h = r.Height;
        using (SolidBrush dark = new SolidBrush(Color.FromArgb(alpha, 40, 40, 40)))
        {
            switch (idx % 6)
            {
                case 0:
                    using (Pen p = new Pen(Color.FromArgb(alpha, 40, 40, 40), Math.Max(2, w / 12)))
                    {
                        g.DrawArc(p, r.X + w / 6, r.Y + h / 3, w * 2 / 3, h / 2, 20, 140);
                        g.FillEllipse(dark, r.X + w / 4, r.Y + h / 4, w / 8, h / 8);
                        g.FillEllipse(dark, r.X + w / 2 + w / 8, r.Y + h / 4, w / 8, h / 8);
                    }
                    break;
                case 1:
                    using (Pen p = new Pen(Color.FromArgb(alpha, 200, 40, 40), Math.Max(3, w / 10)))
                    {
                        g.DrawLine(p, r.X + w / 5, r.Y + h / 4, r.X + w / 2 - w / 8, r.Y + h / 3);
                        g.DrawLine(p, r.X + w * 4 / 5, r.Y + h / 4, r.X + w / 2 + w / 8, r.Y + h / 3);
                    }
                    g.FillEllipse(dark, r.X + w / 4, r.Y + h / 2, w / 8, h / 8);
                    g.FillEllipse(dark, r.X + w / 2 + w / 8, r.Y + h / 2, w / 8, h / 8);
                    break;
                case 2:
                    using (SolidBrush b2 = new SolidBrush(Color.FromArgb(alpha, 70, 150, 230)))
                        g.FillEllipse(b2, r.X + w / 3, r.Y + h / 4, w / 3, h / 2);
                    break;
                case 3:
                    using (SolidBrush b3 = new SolidBrush(Color.FromArgb(alpha, 230, 70, 90)))
                    {
                        g.FillEllipse(b3, r.X + w / 8, r.Y + h / 6, w / 2, h / 2);
                        g.FillEllipse(b3, r.X + w * 3 / 8, r.Y + h / 6, w / 2, h / 2);
                        g.FillPolygon(b3, new Point[] {
                            new Point(r.X + w / 8, r.Y + h / 2),
                            new Point(r.X + w * 7 / 8, r.Y + h / 2),
                            new Point(r.X + w / 2, r.Y + h * 5 / 6) });
                    }
                    break;
                case 4:
                    using (SolidBrush b4 = new SolidBrush(Color.FromArgb(alpha, 70, 150, 230)))
                    {
                        g.FillEllipse(b4, r.X + w / 4, r.Y + h / 2, w / 8, h / 4);
                        g.FillEllipse(b4, r.X + w / 2 + w / 8, r.Y + h / 2, w / 8, h / 4);
                    }
                    break;
                default:
                    using (Font f = new Font("Microsoft YaHei", w * 0.6F, FontStyle.Bold))
                    using (SolidBrush b5 = new SolidBrush(Color.FromArgb(alpha, 60, 60, 200)))
                    {
                        SizeF sz = g.MeasureString("?", f);
                        g.DrawString("?", f, b5, r.X + (w - sz.Width) / 2, r.Y + (h - sz.Height) / 2);
                    }
                    break;
            }
        }
    }
}
