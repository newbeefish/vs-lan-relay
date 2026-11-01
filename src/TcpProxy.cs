using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// TCP 隧道代理：把游戏的 TCP 连接透过 UDP 隧道转发
// 客户端侧：在「对方虚拟 IP:17511」上监听，接住游戏发起的连接
// 主机侧  ：收到请求后连接本机 127.0.0.1:17511（真实游戏）
static class TcpProxy
{
    public const int GameTcpPort = 17511;

    static Dictionary<int, TcpClient> conns = new Dictionary<int, TcpClient>();
    static Dictionary<int, NetworkStream> pending = new Dictionary<int, NetworkStream>();
    static int seq = 0;

    public static Action<int, byte[]> Sender;      // 发送到隧道
    public static Action<string> Logger;
    public static string LocalGameIp = null;       // 本机游戏的绑定 IP（可能是 TAP IP 而非 127.0.0.1）

    static void Log(string s) { try { if (Logger != null) Logger(s); } catch { } }

    public static void Send(int type, int id, byte[] data)
    {
        try
        {
            int n = (data == null) ? 0 : data.Length;
            byte[] p = new byte[3 + n];
            p[0] = (byte)type;
            p[1] = (byte)(id >> 8);
            p[2] = (byte)id;
            if (n > 0) Array.Copy(data, 0, p, 3, n);
            if (Sender != null) Sender(p.Length, p);
        }
        catch { }
    }

    // 开始监听：对方虚拟 IP 上的游戏端口
    public static void StartListener(string bindIp)
    {
        try
        {
            TcpListener l = new TcpListener(IPAddress.Parse(bindIp), GameTcpPort);
            l.Start();
            Log("[TCP] 已在 " + bindIp + ":" + GameTcpPort + " 等待游戏连接");
            Thread t = new Thread(delegate() { AcceptLoop(l); }) { IsBackground = true };
            t.Start();
        }
        catch (Exception ex) { Log("[TCP] 监听失败: " + ex.Message); }
    }

    static void AcceptLoop(TcpListener l)
    {
        while (true)
        {
            try
            {
                TcpClient c = l.AcceptTcpClient();
                int id = Interlocked.Increment(ref seq);
                conns[id] = c;
                Log("[TCP] 游戏已连接，连接号 " + id);
                Send(8, id, null);                     // 请求对端连上它的游戏
                StartPump(id, c);
            }
            catch { Thread.Sleep(50); }
        }
    }

    // 处理隧道来的 TCP 控制/数据
    public static void Handle(int type, int id, byte[] data)
    {
        switch (type)
        {
            case 8:   // 对端请求：连本机游戏
                {
                    // 游戏可能只绑在 TAP IP 上，所以依次尝试多个本地地址
                    string[] targets = new string[] { "127.0.0.1", LocalGameIp };
                    TcpClient ok = null;
                    string lastErr = "";
                    foreach (string ip in targets)
                    {
                        if (string.IsNullOrEmpty(ip)) continue;
                        try
                        {
                            TcpClient c = new TcpClient();
                            c.NoDelay = true;
                            c.Connect(IPAddress.Parse(ip), GameTcpPort);
                            conns[id] = c;
                            ok = c;
                            Log("[TCP] 已连上本机游戏 " + ip + ":" + GameTcpPort + "，连接号 " + id);
                            break;
                        }
                        catch (Exception ex) { lastErr = ip + " -> " + ex.Message; }
                    }
                    if (ok != null)
                    {
                        Send(9, id, new byte[] { 0 });
                        StartPump(id, ok);
                    }
                    else
                    {
                        Log("[TCP] 连接本机游戏失败（已试 127.0.0.1 和 " + (LocalGameIp ?? "无") + "）: " + lastErr);
                        Send(9, id, new byte[] { 1 });
                    }
                }
                break;
            case 9:   // 对端回应
                {
                    bool fail = (data != null && data.Length > 0 && data[0] != 0);
                    if (fail) { EndConn(id); Log("[TCP] 对端连不上它的游戏，连接号 " + id); }
                    else Log("[TCP] 对端已接上游戏，连接号 " + id);
                }
                break;
            case 10:  // 数据
                {
                    TcpClient c;
                    if (conns.TryGetValue(id, out c) && c != null && data != null && data.Length > 0)
                    {
                        try { c.GetStream().Write(data, 0, data.Length); }
                        catch { EndConn(id); }
                    }
                }
                break;
            case 11:  // 关闭
                EndConn(id);
                break;
        }
    }

    static void StartPump(int id, TcpClient c)
    {
        Thread t = new Thread(delegate()
        {
            byte[] buf = new byte[1000];
            try
            {
                NetworkStream ns = c.GetStream();
                while (true)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    byte[] d = new byte[n];
                    Array.Copy(buf, 0, d, 0, n);
                    Send(10, id, d);
                }
            }
            catch { }
            EndConn(id);
        });
        t.IsBackground = true;
        t.Start();
    }

    static void EndConn(int id)
    {
        TcpClient c;
        if (conns.TryGetValue(id, out c))
        {
            conns.Remove(id);
            try { Send(11, id, null); } catch { }
            try { if (c != null) c.Close(); } catch { }
        }
    }
}
