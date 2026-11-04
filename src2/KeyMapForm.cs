using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// 自定义键位窗口
public class KeyMapForm : Form
{
    public static readonly string[] Actions = { "上", "下", "左", "右", "普攻", "远攻", "大招", "跳跃", "冲刺", "连携技", "特殊" };
    public static readonly Keys[] Defaults = { Keys.W, Keys.S, Keys.A, Keys.D, Keys.J, Keys.U, Keys.I, Keys.K, Keys.L, Keys.O, Keys.P };

    static string CfgPath
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsGameRelay");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "keymap.txt");
        }
    }

    // 读取：返回与 Actions 一一对应的键值
    public static Keys[] Load()
    {
        Keys[] k = (Keys[])Defaults.Clone();
        try
        {
            if (File.Exists(CfgPath))
            {
                foreach (string line in File.ReadAllLines(CfgPath))
                {
                    string[] p = line.Split('=');
                    if (p.Length != 2) continue;
                    int idx = Array.IndexOf(Actions, p[0].Trim());
                    if (idx < 0) continue;
                    int v;
                    if (int.TryParse(p[1].Trim(), out v) && v > 0) k[idx] = (Keys)v;
                }
            }
        }
        catch { }
        return k;
    }

    static void Save(Keys[] k)
    {
        try
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < Actions.Length; i++) lines.Add(Actions[i] + "=" + (int)k[i]);
            File.WriteAllLines(CfgPath, lines.ToArray());
        }
        catch { }
    }

    public static string KeyName(Keys k)
    {
        switch (k)
        {
            case Keys.ShiftKey: return "Shift";
            case Keys.ControlKey: return "Ctrl";
            case Keys.Menu: return "Alt";
            case Keys.Space: return "空格";
            case Keys.Oemcomma: return ",";
            case Keys.OemPeriod: return ".";
            case Keys.OemQuestion: return "/";
            case Keys.OemSemicolon: return ";";
            case Keys.OemOpenBrackets: return "[";
            case Keys.OemCloseBrackets: return "]";
            case Keys.OemMinus: return "-";
            case Keys.Oemplus: return "=";
            case Keys.Up: return "↑";
            case Keys.Down: return "↓";
            case Keys.Left: return "←";
            case Keys.Right: return "→";
        }
        return k.ToString();
    }

    // ===== 表情映射：按键 1~6 分别显示哪个表情 =====
    public static readonly string[] EmojiNames = { "41 生气", "44 吐气", "50 皱眉", "52 得意", "62 摸头", "54 大笑" };
    public static int[] EmojiMap = LoadEmojiMap();

    static string EmojiPath
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsGameRelay");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "emojimap.txt");
        }
    }

    public static int[] LoadEmojiMap()
    {
        int[] m = { 0, 1, 2, 3, 4, 5 };
        try
        {
            if (File.Exists(EmojiPath))
            {
                string[] lines = File.ReadAllLines(EmojiPath);
                for (int i = 0; i < 6 && i < lines.Length; i++)
                {
                    int v;
                    if (int.TryParse(lines[i].Trim(), out v) && v >= 0 && v < 6) m[i] = v;
                }
            }
        }
        catch { }
        return m;
    }

    static void SaveEmojiMap(int[] m)
    {
        try
        {
            string[] lines = new string[6];
            for (int i = 0; i < 6; i++) lines[i] = m[i].ToString();
            File.WriteAllLines(EmojiPath, lines);
        }
        catch { }
    }

    readonly Keys[] cur;
    readonly Button[] btns;

    public KeyMapForm()
    {
        cur = Load();
        btns = new Button[Actions.Length];

        Text = "自定义键位";
        ClientSize = new Size(700, 490);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei", 9F);

        Label tip = new Label();
        tip.Text = "点一下键位按钮，然后按你想用的键";
        tip.AutoSize = true; tip.Location = new Point(24, 16);
        tip.ForeColor = Color.Gray;
        Controls.Add(tip);

        for (int i = 0; i < Actions.Length; i++)
        {
            int idx = i;

            Label l = new Label();
            l.Text = Actions[i];
            l.AutoSize = true;
            l.Location = new Point(28, 56 + i * 34);
            Controls.Add(l);

            Button b = new Button();
            b.Size = new Size(150, 28);
            b.Location = new Point(120, 52 + i * 34);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            b.Text = KeyName(cur[idx]);
            b.Click += delegate
            {
                b.Text = "按键中...";
                b.Focus();
            };
            b.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { b.Text = KeyName(cur[idx]); return; }
                // 允许 Shift / Ctrl / Alt 等修饰键
                Keys k = (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LShiftKey) ? Keys.ShiftKey
                       : (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey) ? Keys.ControlKey
                       : (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu) ? Keys.Menu
                       : e.KeyCode;
                cur[idx] = k;
                b.Text = KeyName(k);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            btns[i] = b;
            Controls.Add(b);
        }

        // ---- 右侧：按键 1~6 对应哪个表情 ----
        Label le = new Label();
        le.Text = "数字键 1~6 显示的表情：";
        le.AutoSize = true;
        le.Location = new Point(400, 52);
        le.ForeColor = Color.FromArgb(60, 60, 60);
        Controls.Add(le);

        ComboBox[] combo = new ComboBox[6];
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            Label lk = new Label();
            lk.Text = "按键 " + (i + 1);
            lk.AutoSize = true;
            lk.Location = new Point(400, 86 + i * 38);
            Controls.Add(lk);

            ComboBox cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Location = new Point(450, 82 + i * 38);
            cb.Size = new Size(160, 26);
            cb.Items.AddRange(EmojiNames);
            cb.SelectedIndex = EmojiMap[i];
            combo[i] = cb;
            Controls.Add(cb);
        }

        Button reset = new Button();
        reset.Text = "恢复默认";
        reset.Size = new Size(110, 32);
        reset.Location = new Point(24, 440);
        reset.FlatStyle = FlatStyle.Flat;
        reset.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
        reset.Click += delegate
        {
            for (int i = 0; i < Actions.Length; i++) { cur[i] = Defaults[i]; btns[i].Text = KeyName(cur[i]); }
            for (int i = 0; i < 6; i++) { EmojiMap[i] = i; combo[i].SelectedIndex = i; }
        };
        Controls.Add(reset);

        Button ok = new Button();
        ok.Text = "保存";
        ok.Size = new Size(110, 32);
        ok.Location = new Point(180, 440);
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        ok.Click += delegate
        {
            Save(cur);
            for (int i = 0; i < 6; i++) EmojiMap[i] = combo[i].SelectedIndex;
            SaveEmojiMap(EmojiMap);
            Close();
        };
        Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "取消";
        cancel.Size = new Size(110, 32);
        cancel.Location = new Point(296, 440);
        cancel.FlatStyle = FlatStyle.Flat;
        cancel.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
        cancel.Click += delegate { Close(); };
        Controls.Add(cancel);
    }
}
