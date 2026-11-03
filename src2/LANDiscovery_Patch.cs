using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// ============================================================
//  死神VS火影 联机补丁  ——  只管和游戏换包，不碰网络
//
//  游戏视角：
//     stdin : SEARCH\t<addr>\t<hex>   /  STOP
//     stdout: READY  /  HOST\t127.0.0.1\t17477\t<hex>
//
//  对本机主程序（127.0.0.1:50190）：
//     发出  Q|<hex>        查询包
//     收到  H|<hex>        房间回复（由主程序从隧道拿到）
//
//  对局数据：
//     游戏 TCP 127.0.0.1:17511  →  补丁  →  主程序 TCP 127.0.0.1:17512
//     游戏 UDP 127.0.0.1:17477  →  补丁  →  主程序 UDP 127.0.0.1:17513
// ============================================================

static class Patch
{
    const int GamePort = 17477;
    const int TcpPort = 17511;
    const int MainUdp = 50190;     // 主程序收消息的端口
    const int MainTcp = 17512;     // 主程序 TCP 入口
    const int MainUdpData = 17513; // 主程序 UDP 入口

    static volatile bool running = true;
    static Socket ctrl;            // 和主程序通信
    static UdpClient udpGame;      // 游戏 UDP
    static EndPoint gameEp = null;

    static readonly object outLock = new object();
    static void Out(string s) { lock (outLock) { try { Console.Out.Write(s + "\n"); Console.Out.Flush(); } catch { } } }
    static void Log(string s)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location), "LANDiscovery.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
        }
        catch { }
    }

    static void Main()
    {
        try
        {

            ctrl = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            ctrl.Bind(new IPEndPoint(IPAddress.Any, 0));
            ctrl.ReceiveTimeout = 200;

            Thread r = new Thread(CtrlLoop) { IsBackground = true }; r.Start();
            Thread w = new Thread(Watchdog) { IsBackground = true }; w.Start();

            Log("=== 补丁启动 ===");
            Out("READY");
            StdInLoop();
        }
        catch (Exception ex)
        {
            Log("fatal " + ex.Message);
            Out("ERROR");
        }
    }

    // ---------- 游戏命令 ----------
    static void StdInLoop()
    {
        string line;
        while (running && (line = Console.In.ReadLine()) != null)
        {
            line = line.Replace("\r", "").Trim();
            if (line.Length == 0) continue;
            if (line == "STOP") break;
            if (!line.StartsWith("SEARCH")) continue;

            string[] p = line.Split('\t');
            if (p.Length < 3) continue;

            string msg = p[2];
            Log("查询 -> 主程序");
            SendToMain("Q|" + msg);
        }
        Log("=== 补丁退出 ===");
        running = false;
    }

    static void SendToMain(string s)
    {
        try { ctrl.SendTo(Encoding.ASCII.GetBytes(s), new IPEndPoint(IPAddress.Loopback, MainUdp)); }
        catch { }
    }

    // ---------- 接收主程序的房间回复 ----------
    static void CtrlLoop()
    {
        byte[] buf = new byte[65535];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int n;
            try { n = ctrl.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            catch { break; }
            if (n <= 0) continue;

            string s = Encoding.ASCII.GetString(buf, 0, n);
            if (s.StartsWith("H|"))
            {
                // 格式：H|<对方IP>|<hex>
                string[] p = s.Split(new char[] { '|' }, 3);
                string ip = (p.Length >= 3) ? p[1] : "127.0.0.1";
                string hex = (p.Length >= 3) ? p[2] : s.Substring(2);
                Log("收到房间回复 " + (hex.Length / 2) + "B  对方=" + ip);
                Out("HOST\t" + ip + "\t" + GamePort + "\t" + hex);
            }
        }
    }

    // ---------- TCP 桥 ----------
    static void StartTcpBridge()
    {
        try
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, TcpPort);
            l.Start();
            Thread t = new Thread(delegate ()
            {
                while (running)
                {
                    TcpClient c;
                    try { c = l.AcceptTcpClient(); } catch { break; }
                    Thread th = new Thread(delegate () { PipeTcp(c); }) { IsBackground = true };
                    th.Start();
                }
            }) { IsBackground = true };
            t.Start();
            Log("TCP 桥就绪 127.0.0.1:" + TcpPort);
        }
        catch (Exception ex) { Log("TCP 桥失败: " + ex.Message); }
    }

    static void PipeTcp(TcpClient game)
    {
        TcpClient main = null;
        try
        {
            main = new TcpClient();
            main.NoDelay = true;
            if (!main.ConnectAsync(IPAddress.Loopback, MainTcp).Wait(5000))
            {
                Log("主程序未就绪（TCP）");
                game.Close(); return;
            }
            game.NoDelay = true;
            Log("TCP 已接通主程序");

            NetworkStream a = game.GetStream();
            NetworkStream b = main.GetStream();
            Thread t = new Thread(delegate () { Pump(b, a); }) { IsBackground = true };
            t.Start();
            Pump(a, b);
        }
        catch (Exception ex)
        {
            Log("TCP 桥异常: " + ex.Message);
            try { game.Close(); } catch { }
            try { if (main != null) main.Close(); } catch { }
        }
    }

    static void Pump(NetworkStream from, NetworkStream to)
    {
        byte[] buf = new byte[16384];
        try { while (true) { int n = from.Read(buf, 0, buf.Length); if (n <= 0) break; to.Write(buf, 0, n); to.Flush(); } }
        catch { }
        try { to.Close(); } catch { }
        try { from.Close(); } catch { }
    }

    // ---------- UDP 桥 ----------
    static void StartUdpBridge()
    {
        try
        {
            udpGame = new UdpClient(new IPEndPoint(IPAddress.Loopback, GamePort));
            Thread t = new Thread(UdpGameLoop) { IsBackground = true }; t.Start();
            Log("UDP 桥就绪 127.0.0.1:" + GamePort);
        }
        catch (Exception ex) { Log("UDP 桥失败: " + ex.Message); }
    }

    static void UdpGameLoop()
    {
        // 回程：主程序 → 游戏
        Thread back = new Thread(delegate ()
        {
            UdpClient backUdp = null;
            try { backUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, MainUdpData)); } catch { return; }
            IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] d = backUdp.Receive(ref from);
                    if (gameEp != null) udpGame.Send(d, d.Length, (IPEndPoint)gameEp);
                }
                catch { }
            }
        }) { IsBackground = true };
        back.Start();

        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] d = udpGame.Receive(ref ep);
                gameEp = ep;
                using (UdpClient fwd = new UdpClient())
                    fwd.Send(d, d.Length, new IPEndPoint(IPAddress.Loopback, MainUdpData));
            }
            catch { }
        }
    }

    // ---------- 看门狗 ----------
    static void Watchdog()
    {
        // 测试模式：不检测游戏进程
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--noguard") >= 0)
        {
            Log("看门狗已禁用（测试模式）");
            return;
        }
        while (running)
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
            if (!alive) { Log("游戏已退出"); try { Environment.Exit(0); } catch { } return; }
        }
    }
}
