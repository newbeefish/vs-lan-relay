using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ============================================================
//  死神VS火影 联机助手  ——  主程序
//
//  职责：隧道、握手、确认对端、延迟、收发统计、界面
//  不接触游戏，游戏由补丁对接
//
//  与补丁的本机接口：
//     UDp 50190  收 Q|<hex> 查询   发 H|<hex> 房间回复
//     TCP 17512  接收补丁转来的游戏 TCP
//     UDP 17513  接收补丁转来的游戏 UDP
// ============================================================

static class Program
{
    const int GamePort = 17477;
    const int TcpPort = 17511;
    const int TunnelPort = 50001;
    const int DiscPort = 50002;
    const int PatchUdp = 50190;
    const int PatchTcp = 17512;
    const int PatchUdpData = 17513;
    const string Ver = "R1";

    static string myIp, peerIp, peerHint;
    const bool LossGuard = true;   // 防丢包开关（备份版用 false）
    static int tunSeq = 0;
    static readonly object seqLock = new object();
    static readonly HashSet<int> seenSeq = new HashSet<int>();
    static readonly Queue<int> seenOrder = new Queue<int>();

    static volatile bool peerLocked = false;   // 握手后锁定，避免打架
    static bool selfTest = false;              // --loop 自测：对端设为自己
    static Socket tunSend, tunRecv, patchSock, udpIn, gameUdp, queryOut;
    static TcpListener patchTcp, gameTcpLocal;
    static volatile bool running = false;
    static long fwd = 0, recv = 0;
    static int rtt = -1;
    static DateTime pingAt = DateTime.MinValue;
    static EndPoint patchEp;

    static Form form;
    static Label lblState, lblPeer, lblCount, lblCheck;
    static TextBox txtPeer;
    static Button btn;
    static string logPath;
    static NotifyIcon tray = null;
    static bool reallyExit = false;
    static bool gameSeen = false;

    [STAThread]
    static void Main()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsGameRelay");
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "relay.log");
        try { File.WriteAllText(logPath, ""); } catch { }

        // 结束旧的同名实例，否则端口 50001/50190/17512/17513 会被占用
        try
        {
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("VsRelayUI"))
                if (p.Id != me) { try { p.Kill(); p.WaitForExit(1500); } catch { } }
            Thread.Sleep(500);
        }
        catch { }

        selfTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--loop") >= 0;
        Application.EnableVisualStyles();
        BuildUi();
        Application.Run(form);
    }

    static void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { }
    }

    // ============================================================
    static void BuildUi()
    {
        form = new Form();
        form.Text = "死神VS火影 联机助手";
        form.ClientSize = new Size(560, 420);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.FormBorderStyle = FormBorderStyle.FixedSingle;
        form.MaximizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Microsoft YaHei", 9F);

        Label t = new Label();
        t.Text = "死神VS火影  联机助手";
        t.Font = new Font("Microsoft YaHei", 16F, FontStyle.Bold);
        t.AutoSize = true; t.Location = new Point(28, 20);
        form.Controls.Add(t);

        Label s = new Label();
        s.Text = "游戏由补丁接管，本程序负责隧道与对端握手";
        s.ForeColor = Color.Gray; s.AutoSize = true; s.Location = new Point(30, 60);
        form.Controls.Add(s);

        Label l = new Label();
        l.Text = "隧道对端：自动发现在 Radmin 网段内完成，无需填写"; l.AutoSize = true; l.Location = new Point(30, 100);
        form.Controls.Add(l);

        txtPeer = new TextBox();
        txtPeer.Location = new Point(30, 122); txtPeer.Size = new Size(240, 26);
        txtPeer.ReadOnly = true;
        txtPeer.BackColor = Color.FromArgb(248, 248, 248);
        form.Controls.Add(txtPeer);

        Label l2 = new Label();
        l2.Text = "找到后自动填入并锁定，直到下次点「开始运行」";
        l2.ForeColor = Color.Silver; l2.AutoSize = true; l2.Location = new Point(284, 126);
        form.Controls.Add(l2);

        lblState = Mk("状态：未启动", 30, 164, Color.DimGray);
        lblPeer = Mk("隧道对端：未连接", 30, 186, Color.DimGray);
        lblCheck = Mk("补丁：未检测", 30, 208, Color.DimGray);
        lblCount = Mk("已转发 0   已接收 0   延迟 --", 30, 240, Color.DimGray);
        lblCount.Font = new Font("Microsoft YaHei", 10F);

        btn = new Button();
        btn.Text = "开 始 运 行";
        btn.Location = new Point(30, 280); btn.Size = new Size(220, 54);
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        btn.BackColor = Color.White;
        btn.Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold);
        btn.Cursor = Cursors.Hand;
        btn.Click += delegate { if (running) StopAll(); else StartAll(); };
        form.Controls.Add(btn);

        Label f = new Label();
        f.Text = "游戏目录需安装补丁（安装器会自动完成）";
        f.ForeColor = Color.Silver; f.AutoSize = true; f.Location = new Point(30, 350);
        form.Controls.Add(f);

        System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
        tm.Interval = 500;
        tm.Tick += delegate
        {
            string rttTxt = rtt >= 0 ? ("延迟 " + rtt + " ms")
                          : (peerIp == null ? "延迟 --" : "延迟 测量中...");
            lblCount.Text = "已转发 " + fwd + "   已接收 " + recv + "   " + rttTxt;

            if (rtt >= 0)
                lblCount.ForeColor = rtt < 100 ? Color.FromArgb(30, 150, 70)
                                   : rtt < 200 ? Color.FromArgb(200, 150, 0)
                                               : Color.FromArgb(200, 60, 60);
            else
                lblCount.ForeColor = Color.DimGray;
        };
        tm.Start();

        // 托盘图标
        tray = new NotifyIcon();
        try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { tray.Icon = SystemIcons.Application; }
        tray.Text = "死神VS火影 联机助手";
        tray.Visible = true;

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, delegate { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); });
        menu.Items.Add("退出", null, delegate { reallyExit = true; StopAll(); tray.Visible = false; form.Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); };

        // 点 X → 收进托盘，不退出
        form.FormClosing += delegate (object snd, FormClosingEventArgs e)
        {
            if (!reallyExit)
            {
                e.Cancel = true;
                form.Hide();
                try { tray.ShowBalloonTip(2000, "死神VS火影 联机助手", "已最小化到托盘，游戏关闭后会自动退出", ToolTipIcon.Info); } catch { }
                return;
            }
            StopAll();
            try { tray.Visible = false; tray.Dispose(); } catch { }
        };

        // 游戏退出 → 主程序跟着退出
        new Thread(delegate ()
        {
            int gone = 0;
            while (true)
            {
                Thread.Sleep(2000);
                bool alive = false;
                try
                {
                    foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                    {
                        try
                        {
                            string n = p.ProcessName;
                            if (n.IndexOf("Bleach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("Naruto", StringComparison.OrdinalIgnoreCase) >= 0) { alive = true; break; }
                        }
                        catch { }
                        try { p.Dispose(); } catch { }
                    }
                }
                catch { alive = true; }

                if (alive) { gameSeen = true; gone = 0; continue; }

                // 必须先见过游戏，避免程序先启动时被误杀
                if (gameSeen)
                {
                    gone++;
                    if (gone >= 3)   // 连续 6 秒没有游戏
                    {
                        try
                        {
                            form.BeginInvoke((Action)delegate
                            {
                                reallyExit = true;
                                StopAll();
                                try { tray.Visible = false; } catch { }
                                form.Close();
                            });
                        }
                        catch { }
                        return;
                    }
                }
            }
        }) { IsBackground = true }.Start();
        if (selfTest) { System.Windows.Forms.Timer st = new System.Windows.Forms.Timer(); st.Interval = 600; st.Tick += delegate { st.Stop(); StartAll(); }; st.Start(); }
    }

    static Label Mk(string txt, int x, int y, Color c)
    {
        Label l = new Label();
        l.Text = txt; l.AutoSize = true; l.Location = new Point(x, y); l.ForeColor = c;
        form.Controls.Add(l);
        return l;
    }

    static string ConfigPath
    {
        get
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "VsGameRelay"), "config.ini");
        }
    }
    static string LoadPeer()
    {
        try { if (File.Exists(ConfigPath)) return File.ReadAllText(ConfigPath).Trim(); } catch { }
        return "";
    }
    static void SavePeer(string s)
    {
        try { File.WriteAllText(ConfigPath, s.Trim()); } catch { }
    }

    // ============================================================
    static void StartAll()
    {
        if (selfTest) { peerIp = myIp != null ? myIp : null; peerLocked = true; }
        string manual = txtPeer.Text.Trim();
        if (!selfTest) { peerIp = null; peerLocked = false; }   // 每次启动都重新握手

        myIp = FindRadminIp();
        if (myIp == null)
        {
            MessageBox.Show("没有找到 Radmin VPN 网卡，请先登录 Radmin", "联机助手");
            return;
        }

        // 绑定端口：最多重试 12 次，等旧实例彻底释放
        string bindErr = "";
        bool bound = false;
        for (int attempt = 0; attempt < 12 && !bound; attempt++)
        {
            try { BindAll(); bound = true; }
            catch (Exception ex) { bindErr = ex.Message; CleanupSockets(); Thread.Sleep(400); }
        }
        if (!bound)
        {
            MessageBox.Show("端口占用：" + bindErr + "\n\n请关闭其他实例后重试", "联机助手");
            return;
        }

        running = true;
        new Thread(TunnelLoop) { IsBackground = true }.Start();
        new Thread(PatchLoop) { IsBackground = true }.Start();
        new Thread(UdpLoop) { IsBackground = true }.Start();
        new Thread(TcpAcceptLoop) { IsBackground = true }.Start();
        new Thread(DiscLoop) { IsBackground = true }.Start();
        new Thread(QueryReplyLoop) { IsBackground = true }.Start();

        Log("=== 启动 本机=" + myIp + " 对端=" + peerIp + " ===");
        btn.Text = "停 止 运 行";
        btn.BackColor = Color.FromArgb(240, 248, 240);
        btn.ForeColor = Color.FromArgb(30, 130, 60);
        lblState.Text = "状态：运行中（等补丁接入）";
        lblState.ForeColor = Color.FromArgb(30, 130, 60);
        lblPeer.Text = peerIp != null ? ("隧道对端：正在握手 " + peerIp) : "隧道对端：正在广播寻找";
    }

    // 绑定所有本机端口（可重试）
    static void BindAll()
    {
        CleanupSockets();

        tunRecv = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tunRecv.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        tunRecv.Bind(new IPEndPoint(IPAddress.Any, TunnelPort));
        tunRecv.ReceiveTimeout = 200;

        tunSend = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tunSend.Bind(new IPEndPoint(IPAddress.Any, 0));

        patchSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        patchSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        patchSock.Bind(new IPEndPoint(IPAddress.Loopback, PatchUdp));
        patchSock.ReceiveTimeout = 200;

        queryOut = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        queryOut.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        queryOut.ReceiveTimeout = 200;

        gameUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        gameUdp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        gameUdp.ReceiveTimeout = 200;

        udpIn = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpIn.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpIn.Bind(new IPEndPoint(IPAddress.Loopback, PatchUdpData));
        udpIn.ReceiveTimeout = 200;

        patchTcp = new TcpListener(IPAddress.Loopback, PatchTcp);
        patchTcp.Start();
    }

    static void CleanupSockets()
    {
        try { if (tunRecv != null) { tunRecv.Close(); tunRecv = null; } } catch { }
        try { if (tunSend != null) { tunSend.Close(); tunSend = null; } } catch { }
        try { if (patchSock != null) { patchSock.Close(); patchSock = null; } } catch { }
        try { if (queryOut != null) { queryOut.Close(); queryOut = null; } } catch { }
        try { if (gameUdp != null) { gameUdp.Close(); gameUdp = null; } } catch { }
        try { if (udpIn != null) { udpIn.Close(); udpIn = null; } } catch { }
        try { if (patchTcp != null) { patchTcp.Stop(); patchTcp = null; } } catch { }
    }

    static void StopAll()
    {
        if (!running) return;
        running = false;
        try { tunRecv.Close(); } catch { }
        try { tunSend.Close(); } catch { }
        try { patchSock.Close(); } catch { }
        try { udpIn.Close(); } catch { }
        try { patchTcp.Stop(); } catch { }
        Log("=== 停止 ===");
        btn.Text = "开 始 运 行";
        btn.BackColor = Color.White;
        btn.ForeColor = Color.Black;
        lblState.Text = "状态：已停止";
        lblState.ForeColor = Color.DimGray;
        lblPeer.Text = "隧道对端：未连接";
        try { txtPeer.Text = ""; } catch { }
    }

    // ============================================================
    //  补丁 → 主程序：查询包
    // ============================================================
    static void PatchLoop()
    {
        byte[] buf = new byte[65535];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int n;
            try { n = patchSock.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n <= 0) continue;
            patchEp = any;

            string s = Encoding.ASCII.GetString(buf, 0, n);
            if (!s.StartsWith("Q|")) continue;

            string hex = s.Substring(2).Trim();
            byte[] q = HexToBytes(hex);
            if (q == null) continue;

            lblCheck.Text = "补丁：已接入";
            lblCheck.ForeColor = Color.FromArgb(30, 130, 60);

            // [1][len][原始查询包] → 对方
            if (peerIp == null) { Log("还没有对端，查询丢弃"); return; }
            byte[] env = new byte[2 + q.Length];
            env[0] = 1;
            env[1] = (byte)Math.Min(255, q.Length);
            Array.Copy(q, 0, env, 2, q.Length);
            TunSend(env);
            fwd++;
            Log("转发查询 " + q.Length + "B -> " + peerIp);
        }
    }

    // 隧道发送：加 2 字节序号，同包连发两次（防丢包）
    static void TunSend(byte[] body)
    {
        if (peerTunnel == null) return;
        int seq;
        lock (seqLock) { seq = (tunSeq = (tunSeq + 1) & 0xFFFF); }
        byte[] env = new byte[body.Length + 2];
        env[0] = body[0];
        env[1] = (byte)(seq >> 8);
        env[2] = (byte)seq;
        Array.Copy(body, 1, env, 3, body.Length - 1);
        try { tunSend.SendTo(env, peerTunnel); } catch { }
        try { tunSend.SendTo(env, peerTunnel); } catch { }
    }

    static bool IsDup(int seq)
    {
        lock (seqLock)
        {
            if (seenSeq.Contains(seq)) return true;
            seenSeq.Add(seq);
            seenOrder.Enqueue(seq);
            while (seenOrder.Count > 2048) { seenSeq.Remove(seenOrder.Dequeue()); }
            return false;
        }
    }

    static IPEndPoint peerTunnel { get { return peerIp == null ? null : new IPEndPoint(IPAddress.Parse(peerIp), TunnelPort); } }

    static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        byte[] b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return null;
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }
    static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
    static string BytesToHex(byte[] b, int len)
    {
        StringBuilder sb = new StringBuilder(len * 2);
        for (int i = 0; i < len; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }

    // ============================================================
    //  隧道
    // ============================================================
    static void TunnelLoop()
    {
        byte[] buf = new byte[65535];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int n;
            try { n = tunRecv.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n < 1) continue;

            byte type = buf[0];
            int off = 1;
            if (LossGuard)
            {
                int seq = (buf[1] << 8) | buf[2];
                if (IsDup(seq)) continue;
                off = 3;
            }

            if (type == 1 && n > off)
            {
                int len = buf[off];
                if (n < off + 1 + len) continue;
                try
                {
                    queryOut.SendTo(buf, off + 1, len, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, GamePort));
                    fwd++;
                }
                catch { }
            }
            else if (type == 4 && n > off)
            {
                int len = buf[off];
                if (n < off + 1 + len) continue;
                string hex = "";
                for (int i = 0; i < len; i++) hex += buf[off + 1 + i].ToString("x2");
                if (patchEp != null)
                {
                    byte[] m = Encoding.ASCII.GetBytes("H|" + peerIp + "|" + hex);
                    try { patchSock.SendTo(m, patchEp); recv++; Log("房间回复 -> 补丁 (" + len + "B)"); } catch { }
                }
            }
            else if (type == 2)
            {
                TunSend(new byte[] { 3 });
            }
            else if (type == 3)
            {
                if (pingAt != DateTime.MinValue) rtt = (int)(DateTime.Now - pingAt).TotalMilliseconds;
                lblPeer.Text = "隧道对端：" + peerIp + "（已连接）";
                lblPeer.ForeColor = Color.FromArgb(30, 130, 60);
            }
            else if (type == 12 && n > off)
            {
                byte cid = buf[off];
                TcpLink cl;
                if (links.TryGetValue(cid, out cl))
                {
                    cl.CloseMe();
                    links.Remove(cid);
                    Log("对方关闭连接 id=" + cid);
                }
            }
            else if (type == 10 && n > off)
            {
                byte id = buf[off];
                TcpLink l;
                if (!links.TryGetValue(id, out l))
                {
                    l = TcpLink.ConnectToGame(id);
                    if (l == null) continue;
                    links[id] = l;
                }
                l.Send(buf, off + 1, n - off - 1);
            }
            else if (type == 11)
            {
                try
                {
                    gameUdp.SendTo(buf, off, n - off, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, GamePort));
                    recv++;
                }
                catch { }
            }
        }
    }

    // ---- 本机 UDP（游戏同步包）→ 隧道 ----
    static void UdpLoop()
    {
        // A. 补丁转来的游戏 UDP → 隧道
        new Thread(delegate ()
        {
            byte[] b1 = new byte[65535];
            EndPoint a1 = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                int n;
                try { n = udpIn.ReceiveFrom(b1, ref a1); }
                catch (SocketException) { continue; }
                catch { break; }
                if (n <= 0) continue;
                SendUdpToTunnel(b1, n);
            }
        }) { IsBackground = true }.Start();

        // B. 本机游戏回给我们的 UDP → 隧道（主机侧用）
        byte[] b2 = new byte[65535];
        EndPoint a2 = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int n;
            try { n = gameUdp.ReceiveFrom(b2, ref a2); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n <= 0) continue;
            SendUdpToTunnel(b2, n);
        }
    }

    // 查询的回复（本机游戏 → 我们）→ 隧道，类型 4
    static void QueryReplyLoop()
    {
        byte[] buf = new byte[65535];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int n;
            try { n = queryOut.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n <= 0) continue;
            if (peerIp == null) continue;

            byte[] env = new byte[2 + n];
            env[0] = 4;
            env[1] = (byte)Math.Min(255, n);
            Array.Copy(buf, 0, env, 2, n);
            TunSend(env); fwd++; Log("本机游戏回包 " + n + "B -> 对方");
        }
    }

    static void SendUdpToTunnel(byte[] data, int len)
    {
        if (peerIp == null) return;
        byte[] env = new byte[1 + len];
        env[0] = 11;
        Array.Copy(data, 0, env, 1, len);
        TunSend(env); fwd++;
    }

    // ---- 补丁 TCP → 隧道 ----
    static readonly Dictionary<byte, TcpLink> links = new Dictionary<byte, TcpLink>();

    // 两边各自分配 id 会撞车：按 IP 大小分段，保证全隧道唯一
    static byte IdBase()
    {
        try
        {
            byte[] a = IPAddress.Parse(myIp).GetAddressBytes();
            byte[] b = IPAddress.Parse(peerIp).GetAddressBytes();
            for (int i = 0; i < 4; i++)
            {
                if (a[i] < b[i]) return 1;
                if (a[i] > b[i]) return 128;
            }
        }
        catch { }
        return 1;
    }

    static void TcpAcceptLoop()
    {
        byte nextId = IdBase();
        while (running)
        {
            TcpClient c;
            try { c = patchTcp.AcceptTcpClient(); }
            catch { break; }
            byte id = nextId++;
            TcpLink l = new TcpLink(id, c);
            links[id] = l;
            Log("TCP 接入 id=" + id);
            Thread t = new Thread(delegate () { l.PumpToTunnel(); }) { IsBackground = true };
            t.Start();
        }
    }

    class TcpLink
    {
        public byte Id;
        TcpClient client;
        NetworkStream st;
        public TcpLink(byte id, TcpClient c) { Id = id; client = c; st = c.GetStream(); }

        public static TcpLink ConnectToGame(byte id)
        {
            try
            {
                TcpClient c = new TcpClient();
                c.NoDelay = true;
                if (!c.ConnectAsync(IPAddress.Loopback, TcpPort).Wait(4000)) { c.Close(); return null; }
                TcpLink l = new TcpLink(id, c);
                Log("收到对方连接，已连上本机游戏 id=" + id);
                Thread t = new Thread(delegate () { l.PumpToTunnel(); }) { IsBackground = true };
                t.Start();
                return l;
            }
            catch { return null; }
        }
        public void PumpToTunnel()
        {
            byte[] buf = new byte[16384];
            try
            {
                while (true)
                {
                    int n = st.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    byte[] env = new byte[2 + n];
                    env[0] = 10; env[1] = Id;
                    Array.Copy(buf, 0, env, 2, n);
                    TunSend(env);
                    fwd++;
                }
            }
            catch { }
            try { client.Close(); } catch { }
            try
            {
                if (peerIp != null)
                {
                    byte[] fin = new byte[] { 12, Id };
                    TunSend(fin);
                }
            }
            catch { }
            links.Remove(Id);
        }
        public void CloseMe()
        {
            try { client.Close(); } catch { }
            try { st.Close(); } catch { }
        }

        public void Send(byte[] b, int off, int len)
        {
            try { st.Write(b, off, len); st.Flush(); recv++; } catch { }
        }
    }

    // ============================================================
    static void DiscLoop()
    {
        Socket disc = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        disc.EnableBroadcast = true;
        disc.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { disc.Bind(new IPEndPoint(IPAddress.Any, DiscPort)); } catch { }
        disc.ReceiveTimeout = 250;

        byte[] hello = Encoding.ASCII.GetBytes("VSGR|" + Ver + "|" + myIp);
        byte[] ping = new byte[] { 2 };
        byte[] buf = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        byte[] bc = IPAddress.Parse(myIp).GetAddressBytes();
        bc[1] = 255; bc[2] = 255; bc[3] = 255;
        IPEndPoint bcast = new IPEndPoint(new IPAddress(bc), DiscPort);

        int tick = 0;
        while (running)
        {
            // 每 2 秒广播一次握手请求（已锁定时也发，让对端能重新找到我们）
            if (tick % 8 == 0)
            {
                try { disc.SendTo(hello, bcast); } catch { }
            }

            // 心跳
            if (peerIp != null && tick % 4 == 0)
            {
                pingAt = DateTime.Now;
                TunSend(ping);
            }

            // 收对方的握手广播
            try
            {
                int n = disc.ReceiveFrom(buf, ref any);
                if (n > 0)
                {
                    string s = Encoding.ASCII.GetString(buf, 0, n);
                    if (s.StartsWith("VSGR|"))
                    {
                        string[] p = s.Split('|');
                        if (p.Length >= 3 && p[1] == Ver && p[2] != myIp)
                        {
                            string ip = p[2];
                            if (!peerLocked || peerIp == null)
                            {
                                peerIp = ip;
                                peerLocked = true;
                                Log("握手完成，锁定对端 " + ip);
                                try
                                {
                                    form.BeginInvoke((Action)delegate
                                    {
                                        txtPeer.Text = ip;
                                        lblPeer.Text = "隧道对端：" + ip + "（已握手锁定）";
                                        lblPeer.ForeColor = Color.FromArgb(30, 130, 60);
                                        lblState.Text = "状态：运行中";
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (SocketException) { }
            catch { }

            tick++;
            Thread.Sleep(250);
        }
    }

    static string FindRadminIp()
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            string name = ni.Name + "|" + ni.Description;
            if (name.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    return ua.Address.ToString();
        }
        return null;
    }
}
