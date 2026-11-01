using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(Environment.GetCommandLineArgs()));
    }
}

public class MainForm : Form
{
    const string TapIpPrefix = "10.99.0.";
    const int RelayPort = 50001;
    const int DiscPort = 50002;
    const int TapIoctlMedia = 0x00220018;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, ref int inBuf, int size, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    IntPtr tap = IntPtr.Zero;
    UdpClient relayUdp, discUdp, sendUdp;
    Thread keepThread;
    static int statsTick = 0;
    static int capDiag = 0;
    static int injDiag = 0;
    static DateTime lastPong = DateTime.MinValue;
    static DateTime pingSentAt;
    static string testStep = "";
    static string peerTapIp = null;
    static int dataSeqI = 0;
    static ushort NextSeq() { return (ushort)System.Threading.Interlocked.Increment(ref dataSeqI); }
    static readonly object dupLock = new object();
    static readonly System.Collections.Generic.Queue<ushort> dupOrder = new System.Collections.Generic.Queue<ushort>();
    static readonly System.Collections.Generic.HashSet<ushort> dupSet = new System.Collections.Generic.HashSet<ushort>();

    // 判断是否为重复包（窗口 1024）
    static bool IsDup(ushort seq)
    {
        lock (dupLock)
        {
            if (dupSet.Contains(seq)) return true;
            dupSet.Add(seq);
            dupOrder.Enqueue(seq);
            while (dupOrder.Count > 1024)
            {
                ushort old = dupOrder.Dequeue();
                dupSet.Remove(old);
            }
            return false;
        }
    }
    static DateTime kaSendAt = DateTime.MinValue;
    static int lastRtt = -1;
    static long dupDropped = 0;
    IPEndPoint peerTunnel = null;
    string myTunnelIp = null;
    string myTapIp = null;
    IPAddress myBcast = null;
    string tapGuid = null;
    volatile bool running = false;
    Thread readThread, discThread, netThread;
    List<Socket> capSocks = new List<Socket>();
    Socket injSock = null;
    const bool rawMode = true;      // 原始套接字模式：绕开"游戏选哪块网卡"
    long sent = 0, recv = 0;
    static long netDiag = 0;
    static string myMac = "?";
    static string tapName = "本地连接";
    static byte[] lastReply = null;          // 缓存的房间信息回包
    static DateTime lastReplyAt = DateTime.MinValue;

    Label lblTap, lblTunnel, lblStatus, lblCount;
    Label lblS1, lblS2, lblS3, lblS4;

    Label SelfLine(int y)
    {
        Label l = new Label();
        l.Text = "";
        l.Font = new Font("Microsoft YaHei", 9F);
        l.ForeColor = Color.FromArgb(150, 150, 150);
        l.TextAlign = ContentAlignment.MiddleCenter;
        l.AutoSize = false;
        l.Size = new Size(520 - 40, 19);
        l.Location = new Point(20, y);
        Controls.Add(l);
        return l;
    }
    RoundButton btn;
    System.Windows.Forms.Timer wfTimer;

    bool autoStart = false;

    public MainForm(string[] args)
    {
        foreach (string a in args) if (a == "--auto") autoStart = true;
        Text = "\u6B7B\u795EVS\u706B\u5F71 \u8054\u673A\u52A9\u624B";
        ClientSize = new Size(520, 430);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei", 9F);

        Label title = new Label();
        title.Text = "\u6B7B\u795EVS\u706B\u5F71   \u5C40\u57DF\u7F51\u8054\u673A\u52A9\u624B";
        title.Font = new Font("Microsoft YaHei", 16F, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(28, 28, 28);
        title.AutoSize = true;
        title.Location = new Point(46, 32);
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "\u901A\u8FC7 Radmin VPN \u96A7\u9053\u8F6C\u53D1\u5C40\u57DF\u7F51\u5E7F\u64AD";
        sub.Font = new Font("Microsoft YaHei", 9F);
        sub.ForeColor = Color.FromArgb(150, 150, 150);
        sub.AutoSize = true;
        sub.Location = new Point(50, 72);
        Controls.Add(sub);

        lblTap = InfoLabel("\u8F6C\u53D1\u6A21\u5F0F\uFF1A\u68C0\u6D4B\u4E2D...", 116);
        lblTunnel = InfoLabel("\u96A7\u9053\u5BF9\u7AEF\uFF1A\u672A\u8FDE\u63A5", 150);

        btn = new RoundButton();
        btn.Text = "\u5F00\u59CB\u8F6C\u53D1";
        btn.Size = new Size(260, 54);
        btn.Location = new Point((ClientSize.Width - 260) / 2, 194);
        btn.Click += BtnClick;
        Controls.Add(btn);

        lblStatus = new Label();
        lblStatus.Text = "\u51C6\u5907\u4E2D...";
        lblStatus.Font = new Font("Microsoft YaHei", 9F);
        lblStatus.ForeColor = Color.FromArgb(120, 120, 120);
        lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        lblStatus.AutoSize = false;
        lblStatus.Size = new Size(ClientSize.Width - 40, 22);
        lblStatus.Location = new Point(20, 256);
        Controls.Add(lblStatus);

        lblS1 = SelfLine(280);
        lblS2 = SelfLine(300);
        lblS3 = SelfLine(320);
        lblS4 = SelfLine(340);

        lblCount = new Label();
        lblCount.Text = "";
        lblCount.Font = new Font("Consolas", 9F);
        lblCount.ForeColor = Color.FromArgb(175, 175, 175);
        lblCount.TextAlign = ContentAlignment.MiddleCenter;
        lblCount.AutoSize = false;
        lblCount.Size = new Size(ClientSize.Width - 40, 20);
        lblCount.Location = new Point(20, 370);
        Controls.Add(lblCount);


        Button selfBtn = new Button();
        selfBtn.Text = "\u672C\u673A\u81EA\u68C0";
        selfBtn.Font = new Font("Microsoft YaHei", 8.5F);
        selfBtn.Size = new Size(150, 26);
        selfBtn.Location = new Point((ClientSize.Width - 150) / 2, 396);
        selfBtn.FlatStyle = FlatStyle.Flat;
        selfBtn.FlatAppearance.BorderColor = Color.FromArgb(215, 215, 215);
        selfBtn.ForeColor = Color.FromArgb(110, 110, 110);
        selfBtn.BackColor = Color.White;
        selfBtn.Cursor = Cursors.Hand;
        selfBtn.Click += delegate
        {
            selfBtn.Enabled = false;
            Set(delegate
            {
                lblStatus.Text = "\u6B63\u5728\u81EA\u68C0\uFF0C\u8BF7\u7A0D\u5019...";
                lblStatus.ForeColor = Color.FromArgb(120, 120, 120);
            });
            Thread t = new Thread(delegate()
            {
                bool ok = RunSelfTest();
                Set(delegate
                {
                    lblStatus.ForeColor = ok ? Color.FromArgb(40, 140, 70) : Color.FromArgb(200, 60, 60);
                    selfBtn.Enabled = true;
                });
            });
            t.IsBackground = true;
            t.Start();
        };
        Controls.Add(selfBtn);

        wfTimer = new System.Windows.Forms.Timer();
        wfTimer.Interval = 400;
        wfTimer.Tick += delegate { UpdateCounts(); };
        wfTimer.Start();

        // 按键映射已移除，只保留纯转发

        Shown += delegate
        {
            new Thread(delegate()
            {
                InitEnv();
                if (autoStart)
                {
                    Thread.Sleep(1500);
                    try { Set(delegate { StartRelay(); }); } catch { }
                }
            }) { IsBackground = true }.Start();
        };
        FormClosing += delegate { StopAll(); };
    }

    Label InfoLabel(string text, int y)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = new Font("Microsoft YaHei", 10F);
        l.ForeColor = Color.FromArgb(70, 70, 70);
        l.AutoSize = true;
        l.Location = new Point(50, y);
        Controls.Add(l);
        return l;
    }

    void Set(Action a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch { }
    }

    static readonly object logLock = new object();
    static string logFile = null;

    static void Log(string s)
    {
        try
        {
            if (logFile == null)
                logFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "relay.log");
            lock (logLock)
            {
                try
                {
                    FileInfo fi = new FileInfo(logFile);
                    if (fi.Exists && fi.Length > 3 * 1024 * 1024)
                        File.WriteAllText(logFile, "");
                }
                catch { }
                File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
            }
        }
        catch { }
    }

    static long gameLogCount = 0;

    static void LogGame(string s)
    {
        long n = System.Threading.Interlocked.Increment(ref gameLogCount);
        if (n <= 60 || n % 200 == 0) Log(s + "   #" + n);
    }

    static string DescribeFrame(byte[] f, int len)
    {
        try
        {
            if (len < 34) return "帧 " + len + "B";
            int et = (f[12] << 8) | f[13];
            if (et == 0x0806) return "ARP " + len + "B";
            if (et != 0x0800) return "ethertype 0x" + et.ToString("X4") + " " + len + "B";
            int ihl = (f[14] & 0x0F) * 4;
            string src = f[26] + "." + f[27] + "." + f[28] + "." + f[29];
            string dst = f[30] + "." + f[31] + "." + f[32] + "." + f[33];
            int proto = f[23];
            if (proto == 17 && len >= 14 + ihl + 8)
            {
                int u = 14 + ihl;
                int sp = (f[u] << 8) | f[u + 1];
                int dp = (f[u + 2] << 8) | f[u + 3];
                return "UDP " + src + ":" + sp + " -> " + dst + ":" + dp + "  " + len + "B";
            }
            if (proto == 6 && len >= 14 + ihl + 4)
            {
                int u = 14 + ihl;
                int sp = (f[u] << 8) | f[u + 1];
                int dp = (f[u + 2] << 8) | f[u + 3];
                return "TCP " + src + ":" + sp + " -> " + dst + ":" + dp + "  " + len + "B";
            }
            return "IP proto=" + proto + " " + src + " -> " + dst + "  " + len + "B";
        }
        catch { return "帧 " + len + "B"; }
    }

    static bool IsGameFrame(byte[] f, int len)
    {
        if (len < 34) return false;
        int et = (f[12] << 8) | f[13];
        if (et == 0x0806) return true;
        if (et != 0x0800) return false;
        int ihl = (f[14] & 0x0F) * 4;
        int u = 14 + ihl;
        if (len < u + 4) return false;
        int sp = (f[u] << 8) | f[u + 1];
        int dp = (f[u + 2] << 8) | f[u + 3];
        return sp == 17477 || sp == 17478 || dp == 17477 || dp == 17478 || sp == 17511 || dp == 17511
            || sp == 50001 || dp == 50001;
    }

    void InitEnv()
    {
        Set(delegate { lblStatus.Text = "\u6B63\u5728\u68C0\u6D4B\u7F51\u7EDC\u73AF\u5883..."; });
        EnsureFirewall();

        NetworkInterface radmin = FindAdapter("Radmin");
        NetworkInterface tapNi = FindAdapter("TAP-Windows");

        if (radmin == null)
        {
            Set(delegate
            {
                lblStatus.Text = "\u672A\u627E\u5230 Radmin VPN \u7F51\u5361\uFF0C\u8BF7\u5148\u5B89\u88C5\u5E76\u767B\u5F55 Radmin VPN";
                lblTunnel.Text = "\u96A7\u9053\u5BF9\u7AEF\uFF1ARadmin \u672A\u5C31\u7EEA";
            });
            return;
        }

        myTunnelIp = GetIPv4(radmin);
        Log("=== 启动 === 程序=" + Application.ExecutablePath);
        Log("Radmin IP=" + myTunnelIp + "   广播=" + (myBcast != null ? myBcast.ToString() : "null"));

        // 用网卡真实掩码算广播地址（Radmin 是 /8，不是 /24）
        try
        {
            byte[] ipb = IPAddress.Parse(myTunnelIp).GetAddressBytes();
            byte[] mb = null;
            foreach (UnicastIPAddressInformation u in radmin.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                { mb = u.IPv4Mask.GetAddressBytes(); break; }
            if (mb != null)
            {
                byte[] bb = new byte[4];
                for (int i = 0; i < 4; i++) bb[i] = (byte)(ipb[i] | ~mb[i]);
                myBcast = new IPAddress(bb);
            }
        }
        catch { }

        if (tapNi == null)
        {
            Set(delegate { lblStatus.Text = "\u9996\u6B21\u8FD0\u884C\uFF0C\u6B63\u5728\u5B89\u88C5\u865A\u62DF\u7F51\u5361..."; });
            string err = InstallDriver();
            if (err != "")
            {
                Set(delegate { lblStatus.Text = "\u865A\u62DF\u7F51\u5361\u5B89\u88C5\u5931\u8D25\uFF1A" + err; });
                return;
            }
            Thread.Sleep(3000);
            tapNi = FindAdapter("TAP-Windows");
            if (tapNi == null)
            {
                Set(delegate { lblStatus.Text = "\u9A71\u52A8\u5DF2\u88C5\u4F46\u7F51\u5361\u672A\u51FA\u73B0\uFF0C\u8BF7\u91CD\u542F\u7535\u8111\u518D\u8BD5"; });
                return;
            }
        }

        tapGuid = tapNi.Id;
        tapName = tapNi.Name;
        try { myMac = tapNi.GetPhysicalAddress().ToString(); } catch { myMac = "?"; }
        Log("TAP 网卡=" + tapNi.Name + " GUID=" + tapGuid);
        if (!tapGuid.StartsWith("{")) tapGuid = "{" + tapGuid + "}";

        int last = 2;
        try { last = int.Parse(myTunnelIp.Split('.')[3]); } catch { }
        if (last < 2 || last > 254) last = 2;
        myTapIp = TapIpPrefix + last;

        string cur = GetIPv4(tapNi);
        if (cur != myTapIp)
        {
            Set(delegate { lblStatus.Text = "\u6B63\u5728\u542F\u52A8\u539F\u59CB\u5957\u63A5\u5B57\u6293\u5305 ..."; });
            Run("netsh", "interface ip set address name=\"" + tapNi.Name + "\" static " + myTapIp + " 255.255.255.0");
            Thread.Sleep(3500);
        }

        string tipText = "\u8F6C\u53D1\u6A21\u5F0F\uFF1A\u539F\u59CB\u5957\u63A5\u5B57   \uFF08\u5DF2\u5C31\u7EEA\uFF09";
        string tunText = "\u96A7\u9053\u5BF9\u7AEF\uFF1A\u7B49\u5F85\u8FDE\u63A5...   \uFF08\u672C\u673A " + myTunnelIp + "\uFF09";
        Set(delegate
        {
            lblTap.Text = tipText;
            lblTap.ForeColor = Color.FromArgb(40, 140, 70);
            lblTunnel.Text = tunText;
            lblStatus.Text = "\u73AF\u5883\u5C31\u7EEA\uFF0C\u70B9\u300C\u5F00\u59CB\u8F6C\u53D1\u300D";
        });
    }

    static bool IsLocalAddress(System.Net.IPAddress addr)
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.Equals(addr)) return true;
            }
        }
        catch { }
        return false;
    }

    NetworkInterface FindAdapter(string keyword)
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.Description.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return ni;
        }
        return null;
    }

    string GetIPv4(NetworkInterface ni)
    {
        try
        {
            foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                    return u.Address.ToString();
        }
        catch { }
        return null;
    }

    string InstallDriver()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VSRelay\\driver");
            Directory.CreateDirectory(dir);
            Extract("OemVista.inf", Path.Combine(dir, "OemVista.inf"));
            Extract("tap0901.sys", Path.Combine(dir, "tap0901.sys"));
            Extract("tap0901.cat", Path.Combine(dir, "tap0901.cat"));
            return TapInstaller.Install(Path.Combine(dir, "OemVista.inf"), @"root\tap0901", "TAP-Windows Adapter V9", null);
        }
        catch (Exception ex) { return ex.Message; }
    }

    void Extract(string name, string path)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream s = asm.GetManifestResourceStream("res." + name))
        {
            if (s == null) throw new Exception("missing resource: " + name);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                s.CopyTo(fs);
        }
    }

    static void Run(string exe, string args)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            System.Diagnostics.Process.Start(psi).WaitForExit(8000);
        }
        catch { }
    }

    // 清除系统自动生成的 Block 规则（Block 优先于 Allow，会导致单向不通）
    void EnsureFirewall()
    {
        try
        {
            string exe = Application.ExecutablePath;
            Run("netsh", "advfirewall firewall delete rule name=vsrelay");
            Run("netsh", "advfirewall firewall delete rule name=VSRelay-In");
            Run("netsh", "advfirewall firewall delete rule name=VSRelay-Out");
            Run("netsh", "advfirewall firewall add rule name=VSRelay-In dir=in action=allow program=\"" + exe + "\" enable=yes profile=any");
            Run("netsh", "advfirewall firewall add rule name=VSRelay-Out dir=out action=allow program=\"" + exe + "\" enable=yes profile=any");
        }
        catch { }
    }

    void BtnClick(object sender, EventArgs e)
    {
        if (!running) StartRelay();
        else StopAll();
    }

    void StartRelay()
    {
        if (myTapIp == null) myTapIp = "10.99.0.200";   // 原始套接字模式只把虚拟 IP 当作标识用
        if (tapGuid == null)
        {
            lblStatus.Text = "\u73AF\u5883\u8FD8\u6CA1\u51C6\u5907\u597D\uFF0C\u8BF7\u7A0D\u5019";
            return;
        }
        if (!OpenTap())
        {
            if (!rawMode)
            {
                lblStatus.Text = "\u6253\u5F00\u865A\u62DF\u7F51\u5361\u5931\u8D25\uFF0C\u8BF7\u4EE5\u7BA1\u7406\u5458\u8EAB\u4EFD\u8FD0\u884C";
                return;
            }
            Log("\u865A\u62DF\u7F51\u5361\u672A\u6253\u5F00\uFF08\u539F\u59CB\u5957\u63A5\u5B57\u6A21\u5F0F\u4E0D\u9700\u8981\uFF09");
        }

        running = true;
        btn.Text = "\u505C\u6B62\u8F6C\u53D1";
        btn.BaseColor = Color.FromArgb(225, 70, 70);
        btn.Refresh();

        try
        {
            relayUdp = new UdpClient(RelayPort);
            relayUdp.Client.ReceiveTimeout = 500;
            relayUdp.EnableBroadcast = true;
            try { relayUdp.Client.ReceiveBufferSize = 256 * 1024; } catch { }
            sendUdp = new UdpClient();   // 发送用独立 socket：源端口随机，避免 50001->50001 对称端口被丢
        }
        catch (Exception ex)
        {
            lblStatus.Text = "\u6570\u636E\u7AEF\u53E3\u88AB\u5360\u7528\uFF1A" + ex.Message;
            StopAll();
            return;
        }

        if (rawMode)
        {
            try { injSock = RawNet.OpenInject(); } catch (Exception ex) { Log("注入socket失败: " + ex.Message); }
            foreach (Socket old in capSocks) { try { old.Close(); } catch { } }
            capSocks = RawNet.OpenCapture();
            Log("原始套接字模式：抓包网卡 " + capSocks.Count + " 个");
            foreach (Socket cs in capSocks)
            {
                Socket s2 = cs;
                Thread t2 = new Thread(delegate() { RawCapLoop(s2); }) { IsBackground = true };
                t2.Start();
            }
        }
        else if (readThread == null || !readThread.IsAlive)
        {
            readThread = new Thread(ReadTapLoop) { IsBackground = true };
            readThread.Start();
        }
        discThread = new Thread(DiscLoop) { IsBackground = true };
        discThread.Start();
        netThread = new Thread(NetLoop) { IsBackground = true };
        netThread.Start();
        // 重放补丁已移除：链路正常后不需要，避免重复广播
        if (keepThread == null || !keepThread.IsAlive)
        {
            keepThread = new Thread(KeepAliveLoop) { IsBackground = true };
            keepThread.Start();
        }

        TcpProxy.Sender = delegate(int len, byte[] data)
        {
            try { if (sendUdp != null && peerTunnel != null) sendUdp.Send(data, len, peerTunnel); } catch { }
        };
        TcpProxy.Logger = delegate(string s) { Log(s); };
        TcpProxy.LocalGameIp = myTapIp;

        Log("开始转发 TAP=" + myTapIp + " 端口 " + RelayPort + "/" + DiscPort);
        if (Environment.GetCommandLineArgs().Length > 1 && Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") >= 0)
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--hosttest") >= 0)
            {
                Thread ht = new Thread(HostTest) { IsBackground = true };
                ht.Start();
            }
            else
            {
                Thread st = new Thread(SelfTest) { IsBackground = true };
                st.Start();
            }
        }
        lblStatus.Text = "\u6B63\u5728\u641C\u7D22\u5BF9\u65B9...";
    }

    bool OpenTap()
    {
        try
        {
            if (tap != IntPtr.Zero) return true;
            string path = @"\\.\Global\" + tapGuid + ".tap";
            tap = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (tap == IntPtr.Zero || tap.ToInt64() == -1) return false;
            int st = 1, ret;
            DeviceIoControl(tap, TapIoctlMedia, ref st, 4, IntPtr.Zero, 0, out ret, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    void StopAll()
    {
        running = false;
        try { if (relayUdp != null) relayUdp.Close(); } catch { }
        try { if (discUdp != null) discUdp.Close(); } catch { }
        try { if (sendUdp != null) sendUdp.Close(); } catch { }
        relayUdp = null; discUdp = null; sendUdp = null;
        peerTunnel = null;        try
        {
            btn.Text = "\u5F00\u59CB\u8F6C\u53D1";
            btn.BaseColor = Color.FromArgb(45, 120, 245);
            btn.Refresh();
            if (!IsDisposed) lblStatus.Text = "\u5DF2\u505C\u6B62";
        }
        catch { }
    }

    void DiscLoop()
    {
        try
        {
            discUdp = new UdpClient(DiscPort);
            discUdp.EnableBroadcast = true;
            discUdp.Client.ReceiveTimeout = 500;
            try { discUdp.Client.ReceiveBufferSize = 256 * 1024; } catch { }
        }
        catch { return; }

        IPAddress bcast = myBcast != null ? myBcast : BroadcastOf(myTunnelIp);
        byte[] hello = System.Text.Encoding.ASCII.GetBytes("VSRELAY|H|" + myTunnelIp + "|" + myTapIp);
        int tick = 0;

        while (running)
        {
            try
            {
                if (tick % 4 == 0)
                {
                    if (bcast != null)
                    {
                        try { discUdp.Send(hello, hello.Length, new IPEndPoint(bcast, DiscPort)); } catch { }
                    }
                    try { discUdp.Send(hello, hello.Length, new IPEndPoint(IPAddress.Broadcast, DiscPort)); } catch { }
                }
                tick++;

                IPEndPoint src = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = discUdp.Receive(ref src);
                string s = System.Text.Encoding.ASCII.GetString(d);
                string[] p = s.Split('|');
                if (p.Length >= 4 && p[0] == "VSRELAY")
                {
                    string kind = p[1];
                    string peerTap = p[3];
                    if (!IsLocalAddress(src.Address) && src.Address.ToString() != myTunnelIp)
                    {
                        bool isNew = peerTunnel == null || peerTunnel.Address.ToString() != src.Address.ToString();
                        if (isNew)
                        {
                            peerTunnel = new IPEndPoint(src.Address, RelayPort);
                            peerTapIp = peerTap;
                            Log("发现对端: " + src.Address + "  (对方TAP=" + peerTap + ")");
                            string t = "\u96A7\u9053\u5BF9\u7AEF\uFF1A" + src.Address + "   \uFF08\u5DF2\u8FDE\u63A5\uFF09";
                            Set(delegate
                            {
                                lblTunnel.Text = t;
                                lblTunnel.ForeColor = Color.FromArgb(40, 140, 70);
                                lblStatus.Text = "\u5DF2\u627E\u5230\u5BF9\u65B9\uFF0C\u6B63\u5728\u8F6C\u53D1";
                            });
                        }
                        // 只回应探测包(H)，回包(R)不再回应，否则双方会无限对回
                        if (kind == "H")
                        {
                            byte[] back = System.Text.Encoding.ASCII.GetBytes("VSRELAY|R|" + myTunnelIp + "|" + myTapIp);
                            try { discUdp.Send(back, back.Length, new IPEndPoint(src.Address, DiscPort)); } catch { }
                        }
                    }
                }
            }
            catch (SocketException) { }
            catch { break; }
        }
    }

    static IPAddress BroadcastOf(string ip)
    {
        try
        {
            byte[] a = IPAddress.Parse(ip).GetAddressBytes();
            a[3] = 255;
            return new IPAddress(a);
        }
        catch { return null; }
    }

    void ReadTapLoop()
    {
        byte[] buf = new byte[65536];
        while (true)
        {
            int n; bool ok;
            try { ok = ReadFile(tap, buf, buf.Length, out n, IntPtr.Zero); }
            catch { break; }
            if (!ok || n <= 0) { Thread.Sleep(5); continue; }
            if (!running) continue;   // 已停止：只丢弃，不退出线程

            int len = FrameLen(buf);
            if (len <= 0) continue;
            if (IsGameFrame(buf, len)) LogGame("[TAP->\u5BF9\u7AEF] " + DescribeFrame(buf, len));

            if (peerTunnel != null && relayUdp != null)
            {
                try { sendUdp.Send(buf, len, peerTunnel); sent++; } catch { }
                Thread.Sleep(1);   // 摊平突发，Radmin 隧道对突发 UDP 丢包严重
            }
        }
    }

    void KeepAliveLoop()
    {
        while (running)
        {
            Thread.Sleep(2000);
            if (!running) break;
            try
            {
                if (peerTunnel != null && sendUdp != null)
                    sendUdp.Send(new byte[] { 0x56, 0x53 }, 2, peerTunnel);   // 心跳，防止隧道映射超时
            }
            catch { }
            try
            {
                if (peerTunnel != null && sendUdp != null && statsTick % 2 == 0)
                {
                    byte[] ping = new byte[13];
                    ping[0] = 2;
                    Array.Copy(BitConverter.GetBytes(DateTime.Now.Ticks), 0, ping, 1, 8);
                    kaSendAt = DateTime.Now;
                    sendUdp.Send(ping, ping.Length, peerTunnel);
                }
            }
            catch { }
            try
            {
                statsTick += 2;
                if (statsTick % 10 == 0)
                    Log("[统计] 转发=" + sent + " 接收=" + recv + " 收包=" + netDiag + " 延迟=" + lastRtt + "ms 去重=" + dupDropped + " 对端=" + (peerTunnel != null ? peerTunnel.ToString() : "无"));
            }
            catch { }
        }
    }

    void ReplyLoop()
    {
        while (running)
        {
            Thread.Sleep(1200);
            if (!running) break;
            byte[] rep = lastReply;
            if (rep != null && tap != IntPtr.Zero)
            {
                try { WriteTap(rep); } catch { }
            }
        }
    }

    void RawCapLoop(Socket s)
    {
        byte[] buf = new byte[65536];
        while (running)
        {
            int n;
            try { n = s.Receive(buf); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n <= 0) continue;

            IPAddress src, dst; int sp, dp, off, plen;
            if (!RawNet.TryParseUdp(buf, n, out src, out dst, out sp, out dp, out off, out plen)) continue;
            if (plen <= 0) continue;
            bool inRange = (sp >= 17000 && sp <= 18000) || (dp >= 17000 && dp <= 18000);
            if (inRange && capDiag < 60)
            {
                capDiag++;
                Log("[抓包] " + src + ":" + sp + " -> " + dst + ":" + dp + "  " + plen + "B");
            }
            if (!(sp == 17477 || sp == 17478 || dp == 17477 || dp == 17478)) continue;
            if (peerTapIp != null && src.ToString() == peerTapIp) continue;   // 忽略我们自己伪造注入的包
            if (peerTunnel == null || sendUdp == null || myTapIp == null) continue;

            ushort sq = NextSeq();
            byte[] env = new byte[11 + plen];
            env[0] = 1;
            env[1] = (byte)(sq >> 8); env[2] = (byte)sq;
            byte[] vip = IPAddress.Parse(myTapIp).GetAddressBytes();
            Array.Copy(vip, 0, env, 3, 4);
            env[7] = (byte)(sp >> 8); env[8] = (byte)sp;
            env[9] = (byte)(dp >> 8); env[10] = (byte)dp;
            Array.Copy(buf, off, env, 11, plen);
            // 丢包补偿：同一帧连发两次，接收端按序号去重
            try { sendUdp.Send(env, env.Length, peerTunnel); sent++; } catch { }
            try { sendUdp.Send(env, env.Length, peerTunnel); sent++; } catch { }
            Thread.Sleep(1);
        }
    }

    static System.Collections.Generic.Dictionary<int, UdpClient> injMap = new System.Collections.Generic.Dictionary<int, UdpClient>();
    static string aliasIp = null;

    // 把对端虚拟 IP 加到本机网卡上（辅助地址），这样可以用普通 socket 以它为源地址发送
    void EnsurePeerAlias(string peerIp)
    {
        if (peerIp == null || aliasIp == peerIp) return;
        try
        {
            System.Diagnostics.Process p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "netsh";
            p.StartInfo.Arguments = "interface ipv4 add address name=\"" + tapName + "\" address=" + peerIp + " mask=255.255.255.0";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            p.WaitForExit(6000);
            aliasIp = peerIp;
            Log("已添加辅助地址 " + peerIp + " 到 " + tapName);
            TcpProxy.StartListener(peerIp);
        }
        catch (Exception ex) { Log("添加辅助地址失败: " + ex.Message); }
    }

    void HostTest()
    {
        Thread.Sleep(2000);
        try
        {
            EnsurePeerAlias("10.99.0.158");
            UdpClient l = null;
            for (int i = 0; i < 12; i++)
            {
                try { l = new UdpClient(new IPEndPoint(IPAddress.Parse("10.99.0.158"), 17478)); break; }
                catch { l = null; Thread.Sleep(300); }
            }
            if (l == null) { Log("=== 主机测试：无法绑定 10.99.0.158:17478 ==="); return; }
            l.Client.ReceiveTimeout = 6000;

            byte[] p = new byte[21];
            p[0] = 3; p[1] = 10; p[2] = 11; p[3] = 1;
            p[4] = 9; System.Text.Encoding.ASCII.GetBytes("type").CopyTo(p, 5);
            p[9] = 6; p[10] = 0x13; System.Text.Encoding.ASCII.GetBytes("FIND_HOST").CopyTo(p, 11);
            p[20] = 1;
            Log("=== 主机测试：向 10.99.0.129:17477 发送 FIND_HOST ===");
            InjectLocal(IPAddress.Parse("10.99.0.158"), 17478, 17477, p);
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = l.Receive(ref ep);
                Log("=== 主机测试：成功！游戏回了 " + d.Length + " 字节，来源 " + ep.Address + " ===");
            }
            catch
            {
                Log("=== 主机测试：失败，游戏没有应答（请确认游戏已创建房间并停在房间界面）===");
            }
            l.Close();
        }
        catch (Exception ex) { Log("=== 主机测试异常 === " + ex.Message); }
    }

    void Step(string s)
    {
        testStep = s;
        Log("[自检] " + s);
        Set(delegate { lblStatus.Text = s; lblStatus.ForeColor = Color.FromArgb(120, 120, 120); });
    }

    bool RunSelfTest()
    {
        string[] lines = new string[4];
        bool[] oks = new bool[4];
        int pass = 0;
        try
        {
            // ① 抓包引擎
            try { if (capSocks == null || capSocks.Count == 0) capSocks = RawNet.OpenCapture(); }
            catch { }
            if (capSocks != null && capSocks.Count > 0)
            { oks[0] = true; pass++; lines[0] = "① 抓包引擎：正常（" + capSocks.Count + " 块网卡）"; }
            else lines[0] = "① 抓包引擎：失败（无法打开原始套接字）";

            // ② 本机注入
            bool inj = false;
            try
            {
                if (tap == IntPtr.Zero) OpenTap();
                Thread.Sleep(700);
                UdpClient l = new UdpClient(17801);
                l.Client.ReceiveTimeout = 3500;
                Thread.Sleep(150);
                byte[] payload = new byte[40];
                for (int k = 0; k < payload.Length; k++) payload[k] = 0x5A;
                InjectLocal(IPAddress.Parse("10.99.0.158"), 17477, 17801, payload);
                try { IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0); l.Receive(ref ep); inj = true; }
                catch { }
                l.Close();
            }
            catch { }
            if (inj) { oks[1] = true; pass++; lines[1] = "② 本机注入：正常"; }
            else lines[1] = "② 本机注入：失败（包没能送到本机 socket）";

            // ③ 游戏联通
            try
            {
                bool proc = false;
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    string nm = p.ProcessName;
                    if (nm.IndexOf("Bleach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("Naruto", StringComparison.OrdinalIgnoreCase) >= 0) { proc = true; break; }
                }
                bool p77 = false, p78 = false;
                foreach (System.Net.IPEndPoint ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
                {
                    if (ep.Port == 17477) p77 = true;
                    if (ep.Port == 17478) p78 = true;
                }
                string ports = "17477=" + (p77 ? "有" : "无") + " 17478=" + (p78 ? "有" : "无");
                if (!proc) lines[2] = "③ 游戏联通：游戏未运行";
                else if (p77 || p78)
                {
                    oks[2] = true; pass++;
                    lines[2] = "③ 游戏联通：正常（" + (p77 && p78 ? "双端口 " : (p77 ? "主机 " : "客户端 ")) + ports + "）";
                }
                else lines[2] = "③ 游戏联通：游戏没进入联机界面（" + ports + "）";
            }
            catch (Exception ex) { lines[2] = "③ 游戏联通：检测异常 " + ex.Message; }

            // ④ 对端回环
            if (peerTunnel == null || sendUdp == null)
                lines[3] = "④ 对端回环：未连接（先点「开始转发」）";
            else
            {
                lastPong = DateTime.MinValue;
                long rtt = -1;
                try
                {
                    for (int attempt = 0; attempt < 5 && rtt < 0; attempt++)
                    {
                        byte[] ping = new byte[13];
                        ping[0] = 2;
                        Array.Copy(BitConverter.GetBytes(DateTime.Now.Ticks), 0, ping, 1, 8);
                        pingSentAt = DateTime.Now;
                        try { sendUdp.Send(ping, ping.Length, peerTunnel); } catch { }
                        for (int w = 0; w < 10; w++)
                        {
                            Thread.Sleep(100);
                            if (lastPong != DateTime.MinValue) { rtt = (long)(DateTime.Now - pingSentAt).TotalMilliseconds; break; }
                        }
                    }
                }
                catch { }
                if (rtt >= 0) { oks[3] = true; pass++; lines[3] = "④ 对端回环：正常，往返 " + rtt + " ms"; }
                else lines[3] = "④ 对端回环：无应答（对方程序没开或未连线）";
            }
        }
        catch (Exception ex) { lines[3] = "自检异常：" + ex.Message; }

        for (int i = 0; i < 4; i++)
            if (lines[i] == null) lines[i] = "";

        bool all = (pass == 4);
        Log("=== 自检 " + pass + "/4 ===");
        for (int i = 0; i < 4; i++) if (lines[i].Length > 0) Log("  " + (oks[i] ? "✓ " : "✗ ") + lines[i]);

        Set(delegate
        {
            Label[] ls = new Label[] { lblS1, lblS2, lblS3, lblS4 };
            for (int i = 0; i < 4; i++)
            {
                ls[i].Text = (lines[i].Length > 0 ? (oks[i] ? "✓ " : "✗ ") + lines[i] : "");
                ls[i].ForeColor = oks[i] ? Color.FromArgb(40, 150, 75) : Color.FromArgb(205, 70, 70);
            }
            lblStatus.Text = all ? "自检全部通过（4/4）" : "自检 " + pass + "/4";
            lblStatus.ForeColor = all ? Color.FromArgb(40, 140, 70) : Color.FromArgb(205, 70, 70);
        });
        return all;
    }
    void SelfTest()
    {
        Thread.Sleep(1500);
        RunSelfTest();
    }
    // 注入 socket 同时接收：游戏回复会发到我们伪装的地址上，必须由我们转发回去
    void InjRecvLoop(UdpClient uc, int localPort)
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] d = uc.Receive(ref ep);
                if (d == null || d.Length == 0) continue;
                if (peerTunnel == null || sendUdp == null) continue;
                ushort sq2 = NextSeq();
                byte[] env = new byte[11 + d.Length];
                env[0] = 7;
                env[1] = (byte)(sq2 >> 8); env[2] = (byte)sq2;
                byte[] ipb = ep.Address.GetAddressBytes();
                if (ipb.Length != 4) continue;
                Array.Copy(ipb, 0, env, 3, 4);
                env[7] = (byte)(ep.Port >> 8); env[8] = (byte)ep.Port;
                env[9] = (byte)(localPort >> 8); env[10] = (byte)localPort;
                Array.Copy(d, 0, env, 11, d.Length);
                sendUdp.Send(env, env.Length, peerTunnel);
                sendUdp.Send(env, env.Length, peerTunnel);
                sent++;
                Log("[回程] " + ep.Address + ":" + ep.Port + " -> " + localPort + "  " + d.Length + "B，已转发给对端");
            }
            catch (SocketException) { Thread.Sleep(10); }
            catch { break; }
        }
    }

    void InjectLocal(IPAddress vsrc, int sp, int dp, byte[] payload)
    {
        try
        {
            if (myTapIp == null) return;
            EnsurePeerAlias(vsrc.ToString());
            UdpClient uc;
            if (!injMap.TryGetValue(sp, out uc) || uc == null)
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    try { uc = new UdpClient(new IPEndPoint(vsrc, sp)); break; }
                    catch { uc = null; Thread.Sleep(300); }
                }
                if (uc == null) return;
                uc.Client.ReceiveTimeout = 500;
                injMap[sp] = uc;
                UdpClient held = uc;
                int heldPort = sp;
                Thread rt = new Thread(delegate() { InjRecvLoop(held, heldPort); }) { IsBackground = true };
                rt.Start();
            }
            try
            {
                uc.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse(myTapIp), dp));
            }
            catch
            {
                uc.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, dp));
            }
            if (injDiag < 6) { injDiag++; Log("[注入] 源=" + vsrc + ":" + sp + " -> " + myTapIp + ":" + dp + "  " + payload.Length + "B"); }
        }
        catch (Exception ex)
        {
            if (injDiag < 8) { injDiag++; Log("[注入失败] " + ex.Message); }
        }
    }    void NetLoop()
    {
        while (running)
        {
            try
            {
                if (relayUdp == null) break;
                IPEndPoint src = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = relayUdp.Receive(ref src);
                netDiag++;
                if (d.Length >= 2 && d[0] == 2)   // PING
                {
                    try
                    {
                        if (sendUdp != null)
                        {
                            byte[] pong = new byte[d.Length];
                            pong[0] = 3;
                            Array.Copy(d, 1, pong, 1, d.Length - 1);
                            // 必须回到对方「监听端口」，不能用它的随机源端口
                            IPEndPoint back = peerTunnel != null ? peerTunnel : new IPEndPoint(src.Address, RelayPort);
                            sendUdp.Send(pong, pong.Length, back);
                        }
                    }
                    catch { }
                    continue;
                }
                if (d.Length >= 2 && d[0] == 3)   // PONG
                {
                    lastPong = DateTime.Now;
                    if (kaSendAt != DateTime.MinValue)
                    {
                        int rtt = (int)(DateTime.Now - kaSendAt).TotalMilliseconds;
                        if (rtt > 0 && rtt < 2000)
                        {
                            lastRtt = rtt;
                            int dly = rtt / 2;
                            if (dly < 10) dly = 10;
                            if (dly > 150) dly = 150;

                        }
                    }
                    continue;
                }

                if (d.Length >= 3 && d[0] >= 8 && d[0] <= 11)
                {
                    int tid = (d[1] << 8) | d[2];
                    byte[] tdata = new byte[d.Length - 3];
                    if (tdata.Length > 0) Array.Copy(d, 3, tdata, 0, tdata.Length);
                    TcpProxy.Handle(d[0], tid, tdata);
                    continue;
                }
                if (rawMode && d.Length >= 11 && (d[0] == 1 || d[0] == 7))
                {
                    ushort seq = (ushort)((d[1] << 8) | d[2]);
                    if (IsDup(seq)) { dupDropped++; continue; }   // 重复包，丢弃
                    IPAddress vsrc = new IPAddress(new byte[] { d[3], d[4], d[5], d[6] });
                    int esp = (d[7] << 8) | d[8];
                    int edp = (d[9] << 8) | d[10];
                    byte[] payload = new byte[d.Length - 11];
                    Array.Copy(d, 11, payload, 0, payload.Length);
                    InjectLocal(vsrc, esp, edp, payload);
                    recv++;
                    continue;
                }
                bool wrote = (d.Length >= 14 && tap != IntPtr.Zero) ? WriteTap(d) : false;
                if (netDiag <= 15 || netDiag % 200 == 0)
                {
                    string dm6 = "", sm6 = "";
                    if (d.Length >= 12)
                    {
                        dm6 = d[0].ToString("X2")+d[1].ToString("X2")+d[2].ToString("X2")+d[3].ToString("X2")+d[4].ToString("X2")+d[5].ToString("X2");
                        sm6 = d[6].ToString("X2")+d[7].ToString("X2")+d[8].ToString("X2")+d[9].ToString("X2")+d[10].ToString("X2")+d[11].ToString("X2");
                    }
                    Log("[收包#" + netDiag + "] " + d.Length + "B  写入TAP=" + (wrote ? "成功" : "失败") + "  目标MAC=" + dm6 + "  源MAC=" + sm6 + "  网卡MAC=" + myMac);
                }
                if (wrote)
                {
                    recv++;
                    if (IsGameFrame(d, d.Length)) LogGame("[对端->TAP] " + DescribeFrame(d, d.Length));
                    if (IsFindHostBack(d, d.Length))
                    {
                        lastReply = (byte[])d.Clone();
                        lastReplyAt = DateTime.Now;
                        Log("已缓存房间信息回包 " + d.Length + "B，将周期性重放");
                        try
                        {
                            string txt = System.Text.Encoding.UTF8.GetString(d);
                            int ix = txt.IndexOf("\"updateTime\":");
                            if (ix >= 0)
                            {
                                int st = ix + 13, en = st;
                                while (en < txt.Length && char.IsDigit(txt[en])) en++;
                                if (en > st)
                                {
                                    long ut = long.Parse(txt.Substring(st, en - st));
                                    DateTime dt = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ut).ToLocalTime();
                                    Log("  房间 updateTime=" + dt.ToString("HH:mm:ss") + "  与本机时钟相差 " + ((int)(DateTime.Now - dt).TotalSeconds) + " 秒");
                                }
                            }
                            int ix2 = txt.IndexOf("\"name\":\"");
                            if (ix2 >= 0)
                            {
                                int st2 = ix2 + 8, en2 = st2;
                                while (en2 < txt.Length && txt[en2] != (char)34) en2++;
                                Log("  房间名=" + txt.Substring(st2, en2 - st2));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (SocketException) { }
            catch { break; }
        }
    }

    bool WriteTap(byte[] data)
    {
        try
        {
            int w;
            return WriteFile(tap, data, data.Length, out w, IntPtr.Zero);
        }
        catch { return false; }
    }

    static bool IsFindHostBack(byte[] f, int len)
    {
        if (len < 42) return false;
        if (((f[12] << 8) | f[13]) != 0x0800) return false;
        if (f[23] != 17) return false;
        int ihl = (f[14] & 0x0F) * 4;
        int u = 14 + ihl;
        if (len < u + 8) return false;
        int sp = (f[u] << 8) | f[u + 1];
        int dp = (f[u + 2] << 8) | f[u + 3];
        return sp == 17477 && dp == 17478 && len > 100;
    }

    static int FrameLen(byte[] f)
    {
        if (f.Length >= 34 && ((f[12] << 8) | f[13]) == 0x0800)
        {
            int iptot = (f[16] << 8) | f[17];
            if (iptot >= 20 && iptot + 14 <= f.Length) return iptot + 14;
        }
        if (f.Length >= 42 && ((f[12] << 8) | f[13]) == 0x0806) return 42;
        return 0;
    }

    void UpdateCounts()
    {
        if (running) lblCount.Text = "\u5DF2\u8F6C\u53D1 " + sent + " \u5E27      \u5DF2\u63A5\u6536 " + recv + " \u5E27      \u5EF6\u8FDF " + (lastRtt >= 0 ? lastRtt + " ms" : "--");
        else if (lblCount.Text.Length > 0) lblCount.Text = "";
    }
}

public class RoundButton : Control
{
    public Color BaseColor = Color.FromArgb(45, 120, 245);
    bool hover = false;

    public RoundButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold);
        ForeColor = Color.White;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = hover ? Lighten(BaseColor, 0.10) : BaseColor;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath gp = Round(r, 12))
        using (SolidBrush b = new SolidBrush(c))
        {
            e.Graphics.FillPath(b, gp);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, r, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static Color Lighten(Color c, double f)
    {
        return Color.FromArgb(
            Math.Min(255, (int)(c.R + (255 - c.R) * f)),
            Math.Min(255, (int)(c.G + (255 - c.G) * f)),
            Math.Min(255, (int)(c.B + (255 - c.B) * f)));
    }

    static GraphicsPath Round(Rectangle r, int rad)
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
}
