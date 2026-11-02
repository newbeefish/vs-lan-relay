using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// 死神VS火影 联机助手 安装器
static class Setup
{
    const string AppDir = @"C:\VsGameRelay";
    static Form form;
    static TextBox txtPath;
    static Label lblStatus;
    static Button btnInstall;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        BuildUi();
        DetectAsync();
        Application.Run(form);
    }

    static void BuildUi()
    {
        form = new Form();
        form.Text = "死神VS火影 联机助手 安装";
        form.ClientSize = new Size(640, 330);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.FormBorderStyle = FormBorderStyle.FixedSingle;
        form.MaximizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Microsoft YaHei", 9F);

        Label t = new Label();
        t.Text = "死神VS火影  联机助手";
        t.Font = new Font("Microsoft YaHei", 17F, FontStyle.Bold);
        t.AutoSize = true; t.Location = new Point(30, 22);
        form.Controls.Add(t);

        Label s = new Label();
        s.Text = "补丁接管游戏网络 · 主程序负责隧道与握手 · 无需驱动或虚拟网卡";
        s.ForeColor = Color.Gray; s.AutoSize = true; s.Location = new Point(32, 64);
        form.Controls.Add(s);

        Label l1 = new Label();
        l1.Text = "游戏目录（里面有 Bleach vs. Naruto.exe）：";
        l1.AutoSize = true; l1.Location = new Point(30, 106);
        form.Controls.Add(l1);

        txtPath = new TextBox();
        txtPath.Location = new Point(30, 128); txtPath.Size = new Size(490, 26);
        form.Controls.Add(txtPath);

        Button b = new Button();
        b.Text = "浏览...";
        b.Location = new Point(530, 126); b.Size = new Size(80, 28);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        b.Click += delegate
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选择游戏目录";
            if (d.ShowDialog() == DialogResult.OK) { txtPath.Text = d.SelectedPath; Check(); }
        };
        form.Controls.Add(b);

        lblStatus = new Label();
        lblStatus.Text = "正在查找游戏...";
        lblStatus.AutoSize = true; lblStatus.Location = new Point(30, 170);
        lblStatus.ForeColor = Color.DimGray;
        form.Controls.Add(lblStatus);

        btnInstall = new Button();
        btnInstall.Text = "一 键 安 装";
        btnInstall.Location = new Point(30, 210); btnInstall.Size = new Size(250, 56);
        btnInstall.FlatStyle = FlatStyle.Flat;
        btnInstall.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        btnInstall.BackColor = Color.White;
        btnInstall.Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold);
        btnInstall.Cursor = Cursors.Hand;
        btnInstall.Click += delegate { Install(); };
        form.Controls.Add(btnInstall);

        Label f = new Label();
        f.Text = "安装位置：C:\\VsGameRelay\\   ·   游戏内仅新增一个补丁文件";
        f.ForeColor = Color.Silver; f.AutoSize = true; f.Location = new Point(30, 288);
        form.Controls.Add(f);
    }

    static void DetectAsync()
    {
        System.Threading.Thread th = new System.Threading.Thread(delegate ()
        {
            string d = Detect();
            try
            {
                form.BeginInvoke((Action)delegate
                {
                    if (d != null) { txtPath.Text = d; lblStatus.Text = "已找到游戏目录"; lblStatus.ForeColor = Color.FromArgb(30, 130, 60); }
                    else { lblStatus.Text = "没找到游戏，请点「浏览...」手动选择"; lblStatus.ForeColor = Color.DarkOrange; }
                    Check();
                });
            }
            catch { }
        });
        th.IsBackground = true;
        th.Start();
    }

    static string Detect()
    {
        List<string> dirs = new List<string>();
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)); } catch { }

        Type shT = Type.GetTypeFromProgID("WScript.Shell");
        if (shT != null)
        {
            object sh = Activator.CreateInstance(shT);
            foreach (string d in dirs)
            {
                try
                {
                    foreach (string lnk in Directory.GetFiles(d, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            object sc = shT.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                            string target = (string)shT.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                            if (string.IsNullOrEmpty(target)) continue;
                            if (Path.GetFileName(target).IndexOf("Bleach", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string g = Path.GetDirectoryName(target);
                                if (Looks(g)) return g;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        foreach (DriveInfo di in DriveInfo.GetDrives())
        {
            try
            {
                if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                string r = Scan(di.RootDirectory.FullName, 3);
                if (r != null) return r;
            }
            catch { }
        }

        foreach (string g in new string[] { @"A:\sihuo\死神vs火影_竞技版", @"D:\sihuo\死神vs火影_竞技版" })
            if (Looks(g)) return g;
        return null;
    }

    static string Scan(string dir, int depth)
    {
        try { if (Looks(dir)) return dir; } catch { }
        if (depth <= 0) return null;
        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string n = Path.GetFileName(sub);
                if (n.StartsWith("$") || n.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Users", StringComparison.OrdinalIgnoreCase)) continue;
                string r = Scan(sub, depth - 1);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    static bool Looks(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "Bleach vs. Naruto.exe"))) return false;
            return Directory.Exists(Path.Combine(dir, "assets"));
        }
        catch { return false; }
    }

    static void Check()
    {
        string p = txtPath.Text.Trim();
        if (Looks(p)) { lblStatus.Text = "✅ 目录正确"; lblStatus.ForeColor = Color.FromArgb(30, 130, 60); }
        else if (p.Length > 0) { lblStatus.Text = "⚠ 这个目录里没有 Bleach vs. Naruto.exe"; lblStatus.ForeColor = Color.DarkOrange; }
    }

    static void Install()
    {
        string game = txtPath.Text.Trim();
        if (!Looks(game))
        {
            MessageBox.Show("请选择正确的游戏目录（里面有 Bleach vs. Naruto.exe 的那一层）", "安装");
            return;
        }
        try
        {
            // 1. 主程序
            Directory.CreateDirectory(AppDir);
            foreach (Process p in Process.GetProcessesByName("VsRelayUI")) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
            Extract("VsRelayUI.exe", Path.Combine(AppDir, "VsRelayUI.exe"));

            // 2. 补丁
            string bridge = Path.Combine(game, "assets", "bridge");
            Directory.CreateDirectory(bridge);
            Extract("LANDiscovery.exe", Path.Combine(bridge, "LANDiscovery.exe"));

            // 3. 快捷方式
            Type shT = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(shT);
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "死神VS火影联机助手.lnk");
            object sc = shT.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            shT.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Path.Combine(AppDir, "VsRelayUI.exe") });
            shT.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { AppDir });
            shT.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "死神VS火影 联机助手" });
            shT.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

            lblStatus.Text = "✅ 安装完成";
            lblStatus.ForeColor = Color.FromArgb(30, 130, 60);

            // 4. 启动主程序
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(AppDir, "VsRelayUI.exe"));
            psi.WorkingDirectory = AppDir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            form.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装失败：" + ex.Message, "安装");
        }
    }

    static void Extract(string res, string dst)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string found = null;
        foreach (string n in asm.GetManifestResourceNames())
            if (n.EndsWith(res, StringComparison.OrdinalIgnoreCase)) { found = n; break; }
        if (found == null) throw new Exception("安装包缺少 " + res);
        using (Stream s = asm.GetManifestResourceStream(found))
        using (FileStream f = new FileStream(dst, FileMode.Create, FileAccess.Write))
        {
            byte[] b = new byte[81920];
            int n;
            while ((n = s.Read(b, 0, b.Length)) > 0) f.Write(b, 0, n);
        }
    }
}
