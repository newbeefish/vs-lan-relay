using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Setup
{
    // 日志写到安装程序自己所在目录，失败再退到临时目录（多路径尝试，确保能留下线索）
    static string LogPath = PickLogPath();

    static string PickLogPath()
    {
        try
        {
            string a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install_log.txt");
            File.AppendAllText(a, "");
            return a;
        }
        catch { }
        try
        {
            string b = Path.Combine(Path.GetTempPath(), "vsrelay_setup.log");
            File.AppendAllText(b, "");
            return b;
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), "vsrelay_setup.log");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool MoveFileEx(string existing, string newName, int flags);
    const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    static void Log(string s)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n"); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
        Log("安装开始 args=" + string.Join(" ", args));
        bool noApp = Array.IndexOf(args, "--noapp") >= 0;

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VSRelay");
        // 用带版本号的文件名，避免覆盖正在运行的旧文件导致"访问被拒绝"
        string ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        string stamp = new FileInfo(Assembly.GetExecutingAssembly().Location).LastWriteTime.ToString("MMddHHmm");
        string exe = Path.Combine(dir, "VsRelay_" + stamp + ".exe");

        try
        {
            Directory.CreateDirectory(dir);
            Log("目标目录: " + dir);

            KillRunning();
            WriteProgram(dir, exe);
            CleanOld(dir, exe);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            bool ok1 = MakeShortcut(exe, Path.Combine(desktop, "死神VS火影 联机助手.lnk"));
            bool ok2 = MakeShortcut(exe, Path.Combine(startMenu, "死神VS火影 联机助手.lnk"));
            Log("快捷方式 桌面=" + ok1 + " 开始菜单=" + ok2);

            if (!noApp) { Process.Start(exe); Log("已启动主程序"); }
            else Log("--noapp：跳过启动主程序");
        }
        catch (Exception ex)
        {
            Log("失败: " + ex.GetType().Name + " " + ex.Message);
            MessageBox.Show(
                "安装失败：" + ex.Message + "\r\n\r\n" +
                "最常见的原因是程序还在运行。\r\n" +
                "请先在任务管理器里结束 VsRelay.exe（或从托盘退出），再重新运行本安装程序。",
                "死神VS火影 联机助手",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static void KillRunning()
    {
        for (int round = 0; round < 6; round++)
        {
            Process[] ps = Process.GetProcessesByName("VsRelay");
            if (ps.Length == 0) return;
            foreach (Process p in ps)
            {
                try
                {
                    Log("结束进程 PID " + p.Id);
                    p.Kill();
                }
                catch (Exception ex) { Log("  结束失败: " + ex.Message); }
            }
            foreach (Process p in ps)
            {
                try { p.WaitForExit(2500); } catch { }
            }
            Thread.Sleep(600);
        }
    }

    static void WriteProgram(string dir, string exe)
    {
        byte[] data;
        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream s = asm.GetManifestResourceStream("VsRelay.exe"))
        {
            if (s == null) throw new Exception("内置程序缺失");
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                data = ms.ToArray();
            }
        }

        // 先写临时文件，再替换，避免直接在目标文件上失败
        string tmp = Path.Combine(dir, "VsRelay.tmp");
        Exception last = null;
        for (int i = 0; i < 12; i++)
        {
            try
            {
                File.WriteAllBytes(tmp, data);
                if (File.Exists(exe))
                {
                    try { File.SetAttributes(exe, FileAttributes.Normal); } catch { }
                    File.Delete(exe);
                }
                File.Move(tmp, exe);
                Log("程序写入成功（第 " + (i + 1) + " 次尝试）");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log("  写入尝试 " + (i + 1) + " 失败: " + ex.GetType().Name + " " + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                KillRunning();
                Thread.Sleep(800);
            }
        }

        // 兜底：文件被占用无法替换，安排到下次重启时完成
        try
        {
            string pending = Path.Combine(dir, "VsRelay.new.exe");
            File.WriteAllBytes(pending, data);
            MoveFileEx(exe, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            bool ok = MoveFileEx(pending, exe, MOVEFILE_DELAY_UNTIL_REBOOT);
            Log("已安排重启后替换: " + ok);
            if (ok)
            {
                MessageBox.Show(
                    "程序文件正在被占用，新版本已安排在下一次开机时自动替换。\r\n\r\n" +
                    "请重启电脑后再使用「死神VS火影 联机助手」。",
                    "死神VS火影 联机助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        catch (Exception ex) { Log("安排重启替换失败: " + ex.Message); }

        throw new Exception(last != null ? last.Message : "写入失败");
    }

    // 删除旧版本文件（正在运行的那些删不掉，忽略即可）
    static void CleanOld(string dir, string keep)
    {
        try
        {
            foreach (string f in Directory.GetFiles(dir, "VsRelay*.exe"))
            {
                if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    Log("已删除旧版本 " + Path.GetFileName(f));
                }
                catch (Exception ex) { Log("旧版本删除跳过 " + Path.GetFileName(f) + ": " + ex.GetType().Name); }
            }
        }
        catch { }
    }

    static bool MakeShortcut(string target, string lnkPath)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return false;
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "死神VS火影 局域网联机助手" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, new object[0]);
            return File.Exists(lnkPath);
        }
        catch (Exception ex)
        {
            Log("  快捷方式异常: " + ex.Message);
            return false;
        }
    }
}
