using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    const string Ver = "R2";

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
    static Label lblState, lblPeer, lblCount, lblCheck, lblRole;
    static TextBox txtPeer;
    static Label lblTunnelF;
    static Button btn;
    static string logPath;
    // ===== 联机身份 =====
    static string myId = "玩家";
    static bool iAm1P = false;                  // 实时判定：谁发房间包谁是1P
    static bool hostConfirmed = false;          // 一旦自己建过房，就锁定为1P
    static readonly object peerListLock = new object();
    static readonly Dictionary<string, DateTime> peerSeen = new Dictionary<string, DateTime>();
    static readonly Dictionary<string, string> peerIds = new Dictionary<string, string>();
    static readonly Dictionary<string, int> peerRtt = new Dictionary<string, int>();

    static OverlayForm overlay;
    static KeyHook hook;
    static bool devMode = false;
    static bool gameSeen = false;
    static int gameGone = 0;
    static DateTime lastHookLog = DateTime.MinValue;
    static DateTime lastPingLog = DateTime.MinValue;
    static CheckBox chkSimple;
    static Label lblTunnel, lblHint2, lbListCtl;
    static TextBox txtId;
    static ListBox lstPeers;
    static NotifyIcon tray = null;
    static bool reallyExit = false;

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

        devMode = Array.IndexOf(Environment.GetCommandLineArgs(), "--dev") >= 0;
        selfTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--loop") >= 0;
        Application.EnableVisualStyles();
        BuildUi();
        try
        {
            overlay = new OverlayForm();
            overlay.Show();
            overlay.Hide();

            hook = new KeyHook();
            hook.OnEmojiKey += delegate (int idx)
            {
                if (idx < 0 || idx > 5) return;
                int e = (idx < KeyMapForm.EmojiMap.Length) ? KeyMapForm.EmojiMap[idx] : idx;
                idx = e;
                bool me1p = iAm1P || peerIp == null || !running;
                if (overlay != null) overlay.Emoji(me1p, idx);
                SendEmoji(idx, me1p);
            };
            hook.OnModifierAction += delegate (Keys dest, bool sendUp)
            {
                try { KeyInject.Send(dest, sendUp); } catch { }
            };
            KeyMapForm.EmojiMap = KeyMapForm.LoadEmojiMap();
            HookReload();
            hook.Install();
            hook.StartModifierPolling();
            Log("[钩子] 安装=" + hook.Installed + "  重映射来源=" + (hook.RemapFrom == null ? 0 : hook.RemapFrom.Length));
            Log("[叠加层] 已创建");
        }
        catch { }

        Application.Run(form);
    }

    static void LogV(string s)
    {
        if (devMode) Log(s);
    }

    static void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { }
    }

    // ============================================================
    static void BuildUi()
    {
        form = new Form();
        form.Text = devMode ? "死神VS火影 联机助手 —— 开发版" : "死神VS火影 联机助手";
        form.ClientSize = new Size(820, 413);
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
        l.Text = "隧道对端：自动发现（无需填写）"; l.AutoSize = true; l.Location = new Point(30, 100);
        lblTunnel = l;
        form.Controls.Add(l);

        txtPeer = new TextBox();
        txtPeer.Location = new Point(30, 122); txtPeer.Size = new Size(240, 26);
        txtPeer.ReadOnly = true;
        txtPeer.BackColor = Color.FromArgb(248, 248, 248);
        form.Controls.Add(txtPeer);

        Label l2 = new Label();
        l2.Text = "找到后自动填入并锁定，直到下次点「开始运行」";
        l2.ForeColor = Color.Silver; l2.AutoSize = true; l2.Location = new Point(284, 126);
        lblHint2 = l2;
        form.Controls.Add(l2);

        lblState = Mk("状态：未启动", 30, 164, Color.DimGray);
        lblPeer = Mk("隧道对端：未连接", 30, 186, Color.DimGray);
        lblCheck = Mk("补丁：未检测", 30, 208, Color.DimGray);
        lblRole = Mk("我的角色：未判定", 30, 248, Color.DimGray);
        lblRole.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);

        lblCount = Mk("已转发 0   已接收 0   延迟 --", 30, 270, Color.DimGray);
        lblCount.AutoSize = false;
        lblCount.Size = new Size(500, 22);
        lblCount.Font = new Font("Microsoft YaHei", 10F);

        Label lbId = new Label();
        lbId.Text = "我的 ID："; lbId.AutoSize = true; lbId.Location = new Point(560, 88);
        form.Controls.Add(lbId);

        txtId = new TextBox();
        txtId.Location = new Point(560, 110); txtId.Size = new Size(230, 26);
        txtId.Text = LoadId();
        txtId.TextChanged += delegate
        {
            string v = txtId.Text.Trim();
            myId = v.Length > 0 ? v : "玩家";
            SaveId(myId);
        };
        form.Controls.Add(txtId);

        Label lbList = new Label();
        lbList.Text = "在线对端（点击切换）："; lbList.AutoSize = true; lbList.Location = new Point(560, 146);
        lbListCtl = lbList;
        form.Controls.Add(lbList);

        lstPeers = new ListBox();
        lstPeers.Location = new Point(560, 168); lstPeers.Size = new Size(230, 120);
        lstPeers.Font = new Font("Microsoft YaHei", 9F);
        lstPeers.IntegralHeight = false;
        lstPeers.SelectedIndexChanged += delegate
        {
            if (lstPeers.SelectedIndex < 0) return;
            string ip = ParseIpFromItem(lstPeers.SelectedItem as string);
            if (ip == null) return;
            if (running && ip != peerIp)
            {
                peerIp = ip;
                peerLocked = true;
                Log("切换到对端 " + ip);
                RefreshPeerList();
            }
        };
        form.Controls.Add(lstPeers);

        btn = new Button();
        btn.Text = "开 始 运 行";
        btn.Location = new Point(30, 304); btn.Size = new Size(190, 46);
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        btn.BackColor = Color.White;
        btn.Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold);
        btn.Cursor = Cursors.Hand;
        btn.Click += delegate { if (running) StopAll(); else StartAll(); };
        form.Controls.Add(btn);

        Button btnKeys = new Button();
        btnKeys.Text = "自 定 义 键 位";
        btnKeys.Location = new Point(236, 304); btnKeys.Size = new Size(140, 46);
        btnKeys.FlatStyle = FlatStyle.Flat;
        btnKeys.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnKeys.BackColor = Color.White;
        btnKeys.Font = new Font("Microsoft YaHei", 10F);
        btnKeys.Cursor = Cursors.Hand;
        btnKeys.Click += delegate
        {
            using (KeyMapForm kf = new KeyMapForm()) kf.ShowDialog(form);
            HookReload();          // 保存后立即生效，不用重启
            Log("[钩子] 键位已重新加载，源键数=" + (hook != null && hook.RemapFrom != null ? hook.RemapFrom.Length : 0));
        };
        form.Controls.Add(btnKeys);

        Button btnMode = new Button();
        btnMode.Text = "切 换 模 式";
        btnMode.Location = new Point(392, 304); btnMode.Size = new Size(140, 46);
        btnMode.FlatStyle = FlatStyle.Flat;
        btnMode.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnMode.BackColor = Color.White;
        btnMode.Font = new Font("Microsoft YaHei", 9F);
        btnMode.Cursor = Cursors.Hand;
        btnMode.Click += delegate
        {
            ipv6Mode = !ipv6Mode;
            btnMode.Text = ipv6Mode ? "模 式：IPv6" : "模 式：蓝盾";
            if (ipv6Mode)
            {
                try { StopAll(); } catch { }          // 进入 IPv6：停掉蓝盾全部线程
                try { v6.Start(); } catch { }         // 启动 IPv6
                Log("[模式] 已切到 IPv6，蓝盾已关闭");
            }
            else
            {
                try { v6.Stop(); } catch { }          // 进入蓝盾：停掉 IPv6
                try { v6relay.Stop(); } catch { }     // 并停转发层
                Log("[模式] 已切到蓝盾，IPv6 已关闭");
            }
            ApplyMode();
        };
        form.Controls.Add(btnMode);

        BuildV6Panel();

        chkSimple = new CheckBox();
        chkSimple.Text = "极简模式（关闭叠加层）";
        chkSimple.AutoSize = true;
        chkSimple.Location = new Point(420, 363);
        chkSimple.ForeColor = Color.Gray;
        chkSimple.CheckedChanged += delegate
        {
            if (overlay != null) overlay.Enabled = !chkSimple.Checked;
            Log("[叠加层] " + (chkSimple.Checked ? "极简模式：已关闭渲染" : "已恢复渲染"));
        };
        form.Controls.Add(chkSimple);





        Label f = new Label();
        f.Text = "游戏目录需安装补丁（安装器会自动完成）";
        f.ForeColor = Color.Silver; f.AutoSize = true; f.Location = new Point(30, 366);
        form.Controls.Add(f);

        System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
        tm.Interval = 500;
        tm.Tick += delegate
        {
            // 单人 / 未联机时默认自己是 1P
            // 游戏退出看门狗（与模式无关，始终生效）
            try
            {
                bool alive = false;
                foreach (System.Diagnostics.Process gp in System.Diagnostics.Process.GetProcesses())
                {
                    string gn = gp.ProcessName.ToLower();
                    if (gn.Contains("bleach") || gn.Contains("naruto") || gn.Contains("死神")) { alive = true; break; }
                }
                if (alive) { gameSeen = true; gameGone = 0; }
                else if (gameSeen)
                {
                    gameGone++;
                    if (gameGone >= 3)    // 约 9 秒确认退出
                    {
                        Log("[看门狗] 游戏已退出，程序关闭");
                        try { reallyExit = true; StopAll(); } catch { }
                        try { if (tray != null) tray.Visible = false; } catch { }
                        try { form.Close(); } catch { }
                        return;
                    }
                }
            }
            catch { }

            bool effective1P = iAm1P || peerIp == null || !running;
            if (overlay != null && chkSimple != null) overlay.Enabled = !chkSimple.Checked;
            if (overlay != null)
            {
                overlay.IAm1P = effective1P;
                overlay.Id1P = effective1P ? myId : PeerId();
                overlay.Id2P = effective1P ? PeerId() : myId;
                overlay.ShowIds = true;   // 游戏开着就一直显示（鼠标穿透，不影响操作）
            }

            if (hook != null)
            {
                hook.RemapEnabled = true;
                if (v6.Connected && v6.Peer != null && !v6relay.Running)
                {
                    v6relay.Start(v6.Peer);
                    Log("[IPv6] 双向转发层启动  " + v6.Peer);
                }
                if (!v6.Connected && v6relay.Running) v6relay.Stop();
            }

            if (running)
            {
                lblRole.Text = effective1P ? (peerIp == null ? "我的角色：1P（单人）" : "我的角色：1P（房主）") : "我的角色：2P（加入者）";
                lblRole.ForeColor = effective1P ? Color.FromArgb(200, 90, 0) : Color.FromArgb(30, 100, 180);
            }
            string rttTxt = rtt >= 0 ? ("延迟 " + rtt + " ms")
                          : (peerIp == null ? "延迟 --" : "延迟 测量中...");
            if (ipv6Mode)
            {
                long s6 = v6.Sent, r6 = v6.Recv;
                int r6ms = v6.Rtt;
                if (v6.PeerCode.Length > 0 && txtPeerCode != null && txtPeerCode.Text.Length == 0)
                    txtPeerCode.Text = v6.PeerCode;   // 自动回填对方编号
                if (!v6.Connected && v6.PeerCode.Length == 0 && txtPeerCode != null && txtPeerCode.ReadOnly)
                {
                    txtPeerCode.Text = "";            // 断线后清掉幽灵编号
                    txtPeerCode.ReadOnly = false;
                }
                lblCount.Text = "已发送 " + s6 + "   已接收 " + r6 + "   " + (r6ms >= 0 ? ("延迟 " + r6ms + " ms") : "延迟 --");
                if (overlay != null) overlay.RttText = r6ms >= 0 ? (r6ms + " ms") : "";
                    if (pnlV6 != null)
                    {
                        foreach (Control hc in pnlV6.Controls)
                        {
                            if (hc.Text != null && hc.Text.IndexOf("双方都填了对方地址", StringComparison.Ordinal) >= 0)
                                hc.Visible = v6.MyCode.Length > 0;
                        }
                    }
                    if (v6.MyCode.Length == 0)
                    {
                        lblV6State.Text = "状态：未获取到公网 IPv6\r\n请检查路由器是否开启 IPv6（原生 / DHCPv6）\r\n或联系运营商开通后重启本程序";
                        lblV6State.ForeColor = Color.FromArgb(200, 60, 60);
                    }
                    else if (v6.Connected)
                    {
                        lblV6State.Text = "状态：已连接 · " + (r6ms >= 0 ? ("延迟 " + r6ms + " ms") : "延迟 测量中") + " · 已锁定";
                        lblV6State.ForeColor = Color.FromArgb(30, 130, 60);
                    }
                    else
                    {
                        lblV6State.Text = string.IsNullOrEmpty(v6.LastError) ? "状态：等待对方连接…" : "状态：对方无响应，请确认对方已开启 IPv6 模式";
                        lblV6State.ForeColor = Color.DimGray;
                    }
                return;
            }

            string linkTxt;
            if (ipv6Mode) linkTxt = "链路：IPv6" + (v6.Connected ? " 已连接 " + v6.Rtt + "ms" : " 未连接");
            else linkTxt = (peerIp != null ? "链路：蓝盾" : "链路：未连接");

            lblCount.Text = "已转发 " + fwd + "   已接收 " + recv + "   " + rttTxt + "   " + linkTxt;
            if (overlay != null && v6.Connected) overlay.RttText = v6.Rtt + " ms";
            if (v6.PeerCode.Length > 0 && txtPeerCode != null && txtPeerCode.Text.Length == 0)
                txtPeerCode.Text = v6.PeerCode;   // 自动回填对方编号
            if (v6relay.Running)
                lblCount.Text += "   转发 出" + v6relay.BytesOut + "B/入" + v6relay.BytesIn + "B";
            if (overlay != null) overlay.RttText = (running && rtt >= 0) ? (rtt + " ms") : "";

            if (rtt >= 0)
                lblCount.ForeColor = rtt < 100 ? Color.FromArgb(30, 150, 70)
                                   : rtt < 200 ? Color.FromArgb(200, 150, 0)
                                               : Color.FromArgb(200, 60, 60);
            else
                lblCount.ForeColor = Color.DimGray;
        };
        Relayout();
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
    static string ConfigDir()
    {
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsGameRelay");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    static string LoadId()
    {
        try
        {
            string p = Path.Combine(ConfigDir(), "myid.txt");
            if (File.Exists(p))
            {
                string s = File.ReadAllText(p).Trim();
                if (s.Length > 0) { myId = s; return s; }
            }
        }
        catch { }
        return "玩家";
    }

    static void SaveId(string id)
    {
        try { File.WriteAllText(Path.Combine(ConfigDir(), "myid.txt"), id == null ? "" : id); } catch { }
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
        lblState.Text = "状态：未启动";
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
iAm1P = false;   // 我发了搜索请求（找房间）→ 我是 2P 加入者
    Log("转发查询 " + q.Length + "B -> " + peerIp + "  [我是2P 加入者]");
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

if (type == 1 && n > off)    // 对方发了搜索请求 → 对方找房，我是房主 1P
            {
                iAm1P = true;
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
                    // IPv6 直连时：加入方把对端地址报成 127.0.0.1，由本地转发层转出
                string reportIp = peerIp;
                if (v6.Connected && v6.Peer != null)
                {
                    reportIp = "127.0.0.1";     // 双方都用本机转发，纯 IPv6
                    if (!v6relay.Running)
                    {
                        v6relay.Start(v6.Peer);
                        Log("[IPv6] 双向转发层启动  " + v6.Peer);
                    }
                }
                byte[] m = Encoding.ASCII.GetBytes("H|" + reportIp + "|" + hex);
                    try { patchSock.SendTo(m, patchEp); recv++; if (!hostConfirmed) iAm1P = false; Log("房间回复 -> 补丁 (" + len + "B)  [" + (hostConfirmed ? "我仍是1P" : "我是2P") + "]"); } catch { }
                }
            }
            else if (type == 2)
            {
                TunSend(new byte[] { 3 });
            }
            else if (type == 3)
            {
                if (pingAt != DateTime.MinValue) rtt = (int)(DateTime.Now - pingAt).TotalMilliseconds;
                lock (peerListLock) { if (peerIp != null) peerRtt[peerIp] = rtt; }
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
            else if (type == 20 && n > off + 1)
            {
                int idx = buf[off];
                bool p1 = buf[off + 1] != 0;
                if (overlay != null) overlay.Emoji(p1, idx);
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
            TunSend(env); fwd++; iAm1P = true; hostConfirmed = true; Log("本机游戏回包 " + n + "B -> 对方  [我是1P]");
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
    // ===== 对端列表 =====
    static void RefreshPeerList()
    {
        try
        {
            form.BeginInvoke((Action)delegate
            {
                if (lstPeers == null) return;
                List<string> items = new List<string>();
                List<string> order = new List<string>();
                lock (peerListLock)
                {
                    List<string> dead = new List<string>();
                    foreach (KeyValuePair<string, DateTime> kv in peerSeen)
                        if ((DateTime.Now - kv.Value).TotalSeconds > 12) dead.Add(kv.Key);
                    foreach (string k in dead) { peerSeen.Remove(k); peerRtt.Remove(k); peerIds.Remove(k); }

                    foreach (KeyValuePair<string, string> kv in peerIds)
                        if (peerSeen.ContainsKey(kv.Key)) order.Add(kv.Key);
                    order.Sort();
                    foreach (string ip in order)
                    {
                        int r = peerRtt.ContainsKey(ip) ? peerRtt[ip] : -1;
                        string id = peerIds.ContainsKey(ip) ? peerIds[ip] : "玩家";
                        string mark = (ip == peerIp) ? "★ " : "   ";
                        items.Add(mark + ip + "   " + id + (r >= 0 ? ("   " + r + "ms") : ""));
                    }
                }
                lstPeers.BeginUpdate();
                lstPeers.Items.Clear();
                foreach (string s in items) lstPeers.Items.Add(s);
                lstPeers.EndUpdate();
            });
        }
        catch { }
    }

    // 只有一个对端时自动选中
    static void PeerListAutoPick(string ip)
    {
        lock (peerListLock)
        {
            if (peerIp == null && peerSeen.Count == 1) peerIp = ip;
        }
    }

    static IntPtr FindGameHwnd()
    {
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    string t = p.MainWindowTitle;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
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


    static string PeerId()
    {
        try
        {
            lock (peerListLock)
                if (peerIp != null && peerIds.ContainsKey(peerIp)) return peerIds[peerIp];
        }
        catch { }
        return "";
    }

    static void SendEmoji(int idx, bool isP1)
    {
        if (!running || peerIp == null) return;
        byte[] e = new byte[] { 20, (byte)idx, (byte)(isP1 ? 1 : 0) };
        TunSend(e);
    }

    static void HookReload()
    {
        try
        {
            Keys[] cur = KeyMapForm.Load();
            if (hook != null)
            {
                hook.RemapFrom = cur;
                hook.RemapTo = KeyMapForm.Defaults;
            }
        }
        catch { }
    }

    static string ParseIpFromItem(string s)
    {
        try
        {
            s = s.TrimStart('★', ' ');
            int sp = s.IndexOf(' ');
            if (sp > 0) return s.Substring(0, sp);
        }
        catch { }
        return null;
    }

    // ===== IPv6 模式界面 =====
    static bool ipv6Mode = false;
    static Ipv6Mode v6 = new Ipv6Mode();

    static string MyNumber()
    {
        try
        {
            string p = Path.Combine(ConfigDir(), "machine.txt");
            if (File.Exists(p))
            {
                string s = File.ReadAllText(p).Trim();
                if (s.Length == 6) return s;
            }
            Random r = new Random(Guid.NewGuid().GetHashCode());
            string n = r.Next(100000, 999999).ToString();
            File.WriteAllText(p, n);
            return n;
        }
        catch { return "000000"; }
    }
    static V6Relay v6relay = new V6Relay();
    static Panel pnlV6;
    static TextBox txtMyCode, txtPeerCode;
    static Label lblV6State;

    static void BuildV6Panel()
    {
        pnlV6 = new Panel();
        pnlV6.Location = new Point(20, 84);
        pnlV6.Size = new Size(500, 170);
        pnlV6.Visible = false;
        pnlV6.BackColor = Color.White;
        form.Controls.Add(pnlV6);

        Label l1 = new Label();
        l1.Text = "我的机子编号（复制给对方）：";
        l1.AutoSize = true; l1.Location = new Point(8, 4);
        l1.ForeColor = Color.Gray;
        pnlV6.Controls.Add(l1);

        txtMyCode = new TextBox();
        txtMyCode.Location = new Point(8, 32);
        txtMyCode.Size = new Size(300, 26);
        txtMyCode.ReadOnly = true;
        txtMyCode.BackColor = Color.FromArgb(248, 248, 248);
        txtMyCode.Font = new Font("Consolas", 10F);
        pnlV6.Controls.Add(txtMyCode);

        Button btnCopy = new Button();
        btnCopy.Text = "复制";
        btnCopy.Location = new Point(330, 30);
        btnCopy.Size = new Size(70, 30);
        btnCopy.FlatStyle = FlatStyle.Flat;
        btnCopy.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnCopy.Click += delegate
        {
            try
            {
                if (txtMyCode.Text.Length > 0)
                {
                    Clipboard.SetText(txtMyCode.Text);
                    Log("[IPv6] 地址已复制");
                }
            }
            catch { }
        };
        pnlV6.Controls.Add(btnCopy);

        Label l2 = new Label();
        l2.Text = "对方的机子编号（粘贴后自动握手）：";
        l2.AutoSize = true; l2.Location = new Point(8, 70);
        l2.ForeColor = Color.Gray;
        pnlV6.Controls.Add(l2);

        txtPeerCode = new TextBox();
        txtPeerCode.Location = new Point(8, 92);
        txtPeerCode.Size = new Size(300, 26);
        txtPeerCode.Font = new Font("Consolas", 10F);
        pnlV6.Controls.Add(txtPeerCode);

        Button btnConn = new Button();
        btnConn.Text = "连接";
        btnConn.Location = new Point(330, 90);
        btnConn.Size = new Size(70, 30);
        btnConn.FlatStyle = FlatStyle.Flat;
        btnConn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnConn.Click += delegate
        {
            string code = txtPeerCode.Text.Trim();
            if (code.Length == 0) { Log("[IPv6] 地址为空"); return; }
            if (v6.SetPeer(code))
            {
                Log("[IPv6] 正在连接对方…");
                v6.Start();
                txtPeerCode.ReadOnly = true;
                btnConn.Enabled = false;
            }
            else
            {
                Log("[IPv6] " + v6.LastError);
                MessageBox.Show(v6.LastError ?? "地址无效");
            }
        };
        pnlV6.Controls.Add(btnConn);

        Button btnUnlock = new Button();
        btnUnlock.Text = "解锁";
        btnUnlock.Location = new Point(404, 90);
        btnUnlock.Size = new Size(70, 30);
        btnUnlock.FlatStyle = FlatStyle.Flat;
        btnUnlock.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnUnlock.Click += delegate
        {
            v6.ClearPeer();
            txtPeerCode.Text = "";
            txtPeerCode.ReadOnly = false;
            btnConn.Enabled = true;
            Log("[IPv6] 已解锁，可重新输入地址");
        };
        pnlV6.Controls.Add(btnUnlock);

        lblV6State = new Label();
        lblV6State.Text = "状态：等待操作";
        lblV6State.AutoSize = false;
        lblV6State.Size = new Size(470, 54);
        lblV6State.Location = new Point(8, 123);
        lblV6State.ForeColor = Color.DimGray;
        pnlV6.Controls.Add(lblV6State);

        Label l3 = new Label();
        l3.Text = "提示：双方都填了对方地址后会自动握手，成功后自动锁定";
        l3.AutoSize = true; l3.Location = new Point(8, 146);
        l3.ForeColor = Color.Silver;
        pnlV6.Controls.Add(l3);
    }

    static Panel pnlRadmin;

    // 把控件搬进容器并设置相对坐标
    static void MoveTo(Panel p, Control c, int x, int y)
    {
        if (c == null || p == null) return;
        try
        {
            if (c.Parent != null) c.Parent.Controls.Remove(c);
            p.Controls.Add(c);
            c.Location = new Point(x, y);
        }
        catch { }
    }

    static Control FindByText(string key)
    {
        foreach (Control c in form.Controls)
        {
            try { if (c.Text != null && c.Text.IndexOf(key, StringComparison.Ordinal) >= 0) return c; }
            catch { }
        }
        return null;
    }

    // 统一重排：两个容器互斥 + 其余控件固定坐标
    static void Relayout()
    {
        // ---- 容器 ----
        if (pnlRadmin == null)
        {
            pnlRadmin = new Panel();
            pnlRadmin.Location = new Point(24, 84);
            pnlRadmin.Size = new Size(500, 170);
            pnlRadmin.BackColor = Color.White;
        }
        if (pnlRadmin.Parent == null) form.Controls.Add(pnlRadmin);

        if (pnlV6 == null) BuildV6Panel();
        pnlV6.Location = new Point(24, 84);
        pnlV6.Size = new Size(500, 170);
        pnlV6.BringToFront();

        // ---- 蓝盾控件搬进容器 ----
        Control cTunnel = FindByText("隧道对端：自动发现");
        Control cHint = FindByText("找到后自动填入");
        if (cHint == null) cHint = FindByText("点击后自动握手");
        MoveTo(pnlRadmin, cTunnel, 8, 8);
        MoveTo(pnlRadmin, txtPeer, 8, 32);
        MoveTo(pnlRadmin, cHint, 210, 62);
        MoveTo(pnlRadmin, lblState, 8, 74);
        MoveTo(pnlRadmin, lblPeer, 8, 98);
        MoveTo(pnlRadmin, lblCheck, 8, 122);

        // ---- 右侧列 ----
        Control cId = FindByText("我的 ID"); if (cId != null) cId.Location = new Point(556, 84);
        if (txtId != null) { txtId.Location = new Point(556, 106); txtId.Size = new Size(230, 26); }
        Control cList = FindByText("在线对端"); if (cList != null) cList.Location = new Point(556, 142);
        if (lstPeers != null) { lstPeers.Location = new Point(556, 164); lstPeers.Size = new Size(230, 124); }

        // ---- 底部共用 ----
        if (lblRole != null) lblRole.Location = new Point(30, 259);
        if (lblCount != null) { lblCount.Location = new Point(30, 283); }
        if (btn != null) { btn.Location = new Point(30, 313); btn.Size = new Size(190, 46); }
        Control cKeys = FindByText("自 定 义 键 位"); if (cKeys != null) { cKeys.Location = new Point(236, 313); cKeys.Size = new Size(140, 46); }
        Control cMode = FindByText("模 式：");
        if (cMode == null) cMode = FindByText("切 换 模 式");
        if (cMode != null) { cMode.Visible = true; cMode.Location = new Point(392, 313); cMode.Size = new Size(140, 46); }
        if (chkSimple != null) chkSimple.Location = new Point(420, 363);
        Control cFoot = FindByText("游戏目录需安装补丁");
        if (cFoot != null) cFoot.Location = new Point(30, 367);

        ApplyMode();
    }

    static void ApplyMode()
    {
        bool v6m = ipv6Mode;
        if (v6m) { try { v6.Start(); } catch { } }   // 进 IPv6 就开监听，对方填了地址即可连入

        if (pnlRadmin != null) pnlRadmin.Visible = !v6m;
        if (pnlV6 != null) pnlV6.Visible = v6m;
        if (lstPeers != null) lstPeers.Visible = !v6m;
        Control cList2 = FindByText("在线对端"); if (cList2 != null) cList2.Visible = !v6m;

        if (v6m)
        {
            if (txtMyCode != null)
            {
                string mc = v6.MyCode;
                txtMyCode.Text = mc.Length > 0 ? (mc + MyNumber()) : "未获取到 IPv6";
                if (mc.Length == 0 && lblV6State != null)
                {
                    lblV6State.Text = "状态：未获取到公网 IPv6\n请检查路由器是否开启 IPv6（原生/DHCPv6），\n或联系运营商开通，然后重启本程序。";
                    lblV6State.ForeColor = Color.FromArgb(200, 60, 60);
                }
            }
        }
        else
        {
            v6.Stop();
            v6.ClearPeer();
        }
        Log("[模式] " + (v6m ? "IPv6 直连" : "蓝盾 VPN"));
    }

    static void DiscLoop()
    {
        Socket disc = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        disc.EnableBroadcast = true;
        disc.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { disc.Bind(new IPEndPoint(IPAddress.Any, DiscPort)); } catch { }
        disc.ReceiveTimeout = 250;

        string idEnc = Uri.EscapeDataString(myId == null ? "玩家" : myId);
        byte[] helloV6 = Encoding.ASCII.GetBytes("VSGR|R3|" + myIp + "|" + idEnc + "|" + v6.MyCode);
        byte[] helloNew = Encoding.ASCII.GetBytes("VSGR|R2|" + myIp + "|" + idEnc);
        byte[] helloOld = Encoding.ASCII.GetBytes("VSGR|R1|" + myIp);
        byte[] ping = new byte[] { 2 };
        byte[] buf = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        byte[] bc = IPAddress.Parse(myIp).GetAddressBytes();
        bc[1] = 255; bc[2] = 255; bc[3] = 255;
        IPEndPoint bcast = new IPEndPoint(new IPAddress(bc), DiscPort);

        int tick = 0;
        while (running)
        {
            // ---- 广播握手（三种格式并存，兼容各版本）----
            if (tick % 8 == 0)
            {
                try { disc.SendTo(helloV6, bcast); } catch { }
            }

            // ---- 蓝盾心跳 ----
            if (peerIp != null && tick % 4 == 0)
            {
                pingAt = DateTime.Now;
                TunSend(ping);
            }

            // ---- 收握手 ----
            try
            {
                int n = disc.ReceiveFrom(buf, ref any);
                if (n > 0)
                {
                    string s = Encoding.ASCII.GetString(buf, 0, n);
                    if (s.StartsWith("VSGR|"))
                    {
                        string[] p = s.Split('|');
                        bool verOk = p.Length >= 3 && (p[1] == "R1" || p[1] == "R2" || p[1] == "R3");
                        if (verOk && p[2] != myIp)
                        {
                            string ip = p[2];
                             string pid = null;
                             try { if (p.Length >= 4 && p[3].Length > 0) pid = Uri.UnescapeDataString(p[3]); } catch { }
                             if (string.IsNullOrEmpty(pid))
                             {
                                 lock (peerListLock) { string old; if (peerIds.TryGetValue(p[2], out old)) pid = old; }
                                 if (string.IsNullOrEmpty(pid)) pid = "玩家";
                             }

                            // 记录到在线列表
                            lock (peerListLock)
                            {
                                peerSeen[ip] = DateTime.Now;
                                peerIds[ip] = pid;
                                if (!peerRtt.ContainsKey(ip)) peerRtt[ip] = -1;
                            }
                            RefreshPeerList();

                            // R3 带 IPv6 压缩码 → 自动优先尝试 IPv6
                            if (p.Length >= 5 && p[4].Length > 0 && !v6.Connected && v6.Peer == null)
                            {
                                if (v6.SetPeer(p[4]))
                                {
                                    v6.Start();
                                    Log("[IPv6] 收到 " + ip + " 的公网地址，自动优先试连");
                                        try
                                        {
                                            form.BeginInvoke((Action)delegate
                                            {
                                                if (txtPeerCode != null && txtPeerCode.Text.Length == 0)
                                                    txtPeerCode.Text = p[4];
                                            });
                                        }
                                        catch { }
                                }
                            }

                            if (!peerLocked || peerIp == null)
                            {
                                peerIp = ip;
                                peerLocked = true;
                            LogV("[DEBUG] 收到握手包");
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
