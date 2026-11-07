using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// IPv6 直连模式：地址压缩 / 自动握手 / 延迟测量
public class Ipv6Mode
{
    public const int Port = 50021;

    // ---- 128 位地址打包 + 混淆 + Base64url → 22 字符 ----
    static readonly byte[] Key = {
        0x5A,0xC3,0x17,0xE9,0x2B,0x84,0x6D,0xF1,
        0x3C,0x95,0x4E,0xA7,0x08,0xD2,0x76,0x1B };

    public static string Pack(IPAddress v6)
    {
        byte[] b = v6.GetAddressBytes();
        if (b.Length != 16) return null;
        for (int i = 0; i < 16; i++) b[i] ^= Key[i];
        string s = Convert.ToBase64String(b);
        return s.Replace('+', '-').Replace('/', '_').Replace("=", "");
    }

    public static IPAddress Unpack(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        try
        {
            string raw = code.Trim();
            if (raw.Length > 22) raw = raw.Substring(0, raw.Length - 6);   // 去掉尾部 6 位机子编号
            string s = raw.Replace('-', '+').Replace('_', '/');
            while (s.Length % 4 != 0) s += "=";
            byte[] b = Convert.FromBase64String(s);
            if (b.Length != 16) return null;
            for (int i = 0; i < 16; i++) b[i] ^= Key[i];
            IPAddress a = new IPAddress(b);
            if (a.AddressFamily != AddressFamily.InterNetworkV6) return null;
            if (a.IsIPv6LinkLocal || IPAddress.IsLoopback(a)) return null;
            return a;
        }
        catch { return null; }
    }

    static DateTime v6CheckAt = DateTime.MinValue;
    static bool v6Usable = false;

    // 真实可用性检测：能连上公网 IPv6 才算有
    public static bool V6Usable()
    {
        if ((DateTime.Now - v6CheckAt).TotalSeconds < 10) return v6Usable;
        v6CheckAt = DateTime.Now;
        v6Usable = false;
        string[] tests = { "2400:3200::1", "240c::6666", "2001:4860:4860::8888" };
        foreach (string t in tests)
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
                {
                    IAsyncResult ar = s.BeginConnect(new IPEndPoint(IPAddress.Parse(t), 53), null, null);
                    if (ar.AsyncWaitHandle.WaitOne(900) && s.Connected) { v6Usable = true; break; }
                }
            }
            catch { }
        }
        return v6Usable;
    }

    // ---- 本机公网 IPv6 ----
    public static IPAddress Local()
    {
        if (!V6Usable()) return null;
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    IPAddress a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || IPAddress.IsLoopback(a)) continue;
                    if (ua.SuffixOrigin == SuffixOrigin.Random) continue;   // 临时地址会轮换，跳过
                    byte[] b = a.GetAddressBytes();
                    if ((b[0] & 0xFE) == 0xFC) continue;      // fc00::/7 唯一本地地址
                    if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) continue;  // Teredo
                    if (b[0] == 0x20 && b[1] == 0x02) continue;                                  // 6to4
                    if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) continue;
                    return a;
                }
            }
        }
        catch { }
        return null;
    }

    // ---- 状态 ----
    public volatile bool Running = false;
    public volatile IPAddress Peer = null;
    public volatile int Rtt = -1;
    public volatile bool Connected = false;
    public volatile string LastError = "";
    public volatile string PeerCode = "";      // 对方的编号（用于界面回填）
    int failCount = 0;                         // 连续失败计数，避免一次失败就断开

    public long Sent = 0, Recv = 0;

    TcpListener listener;
    Socket peerSock = null;
    Thread th;

    public string MyCode { get { IPAddress a = Local(); return a == null ? "" : Pack(a); } }
    public bool HasV6 { get { return Local() != null; } }

    public void Start()
    {
        if (Running) return;
        Running = true;
        LastError = "";
        th = new Thread(Loop) { IsBackground = true };
        th.Start();
    }

    public void Stop()
    {
        Running = false;
        Connected = false;
        Rtt = -1;
        try { if (listener != null) listener.Stop(); } catch { }
        try { if (peerSock != null) peerSock.Close(); } catch { }
        listener = null; peerSock = null;
    }

    // 用户填入对方的压缩码
    public bool SetPeer(string code)
    {
        IPAddress a = Unpack(code);
        if (a == null) { LastError = "地址无效"; return false; }
        if (a.Equals(Local())) { LastError = "不能连自己"; return false; }
        Peer = a;
        Connected = false;
        Rtt = -1;
        return true;
    }

    public void ClearPeer()
    {
        Peer = null;
        Connected = false;
        Rtt = -1;
        LastError = "";
        try { if (peerSock != null) peerSock.Close(); } catch { }
        peerSock = null;
    }

    void Loop()
    {
        // 监听
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, Port);
            listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            listener.Start();
        }
        catch (Exception e) { LastError = "监听失败: " + e.Message; }

        while (Running)
        {
            try
            {
                if (listener != null && listener.Pending())
                {
                    Socket c = listener.AcceptSocket();
                    try
                    {
                        c.ReceiveTimeout = 3000;
                        byte[] buf = new byte[64];
                        int n = c.Receive(buf);
                        if (n > 0)
                        {
                            string s = Encoding.ASCII.GetString(buf, 0, n).Trim();
                            IPAddress from = ((IPEndPoint)c.RemoteEndPoint).Address;
                            if (s.StartsWith("V6|"))
                            {
                                // 对方连过来 → 自动锁定
                                Peer = from;
                                PeerCode = s.Substring(3).Trim();   // 对方的编号
                                failCount = 0;
                                Connected = true;
                                byte[] ack = Encoding.ASCII.GetBytes("V6OK|" + Pack(Local()) + "\n");
                                c.Send(ack);
                                Recv++;
                            }
                        }
                    }
                    catch { }
                    finally { try { c.Close(); } catch { } }
                }
            }
            catch { }

            // 主动连接 + 心跳测延迟
            try
            {
                if (Peer != null && !Connected)
                {
                    using (Socket s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
                    {
                        s.ReceiveTimeout = 2000;
                        s.Connect(new IPEndPoint(Peer, Port));
                        s.Send(Encoding.ASCII.GetBytes("V6|" + Pack(Local()) + "\n"));
                        byte[] buf = new byte[64];
                        int n = s.Receive(buf);
                        if (n > 0)
                        {
                            string r = Encoding.ASCII.GetString(buf, 0, n).Trim();
                            if (r.StartsWith("V6OK|")) PeerCode = r.Substring(5).Trim();
                            Connected = true; failCount = 0; Sent++;
                        }
                    }
                }
                else if (Peer != null && Connected)
                {
                    DateTime t0 = DateTime.Now;
                    using (Socket s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
                    {
                        s.ReceiveTimeout = 2000;
                        s.Connect(new IPEndPoint(Peer, Port));
                        s.Send(Encoding.ASCII.GetBytes("V6P|" + Pack(Local()) + "\n"));
                        byte[] buf = new byte[8];
                        s.Receive(buf);
                        Rtt = (int)(DateTime.Now - t0).TotalMilliseconds;
                        Sent++;
                    }
                }
            }
            catch (Exception e)
            {
                failCount++;
                if (failCount >= 3 && Connected)
                {
                    Connected = false; Rtt = -1;
                    PeerCode = "";
                    Peer = null;          // 清掉假对端，避免幽灵状态一直挂着
                }
                if (Peer != null) LastError = e.Message;
            }

            Thread.Sleep(1000);
        }
    }
}
