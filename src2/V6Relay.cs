using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// IPv6 兼容层（双向）：
//  · 本地游戏 → 127.0.0.1:17511/17477 → 转发到对方 IPv6
//  · 对方 IPv6 → [::]:17511/17477   → 转发给本地游戏
public class V6Relay
{
    public const int TcpPort = 17511;
    public const int UdpPort = 17477;
    const int ClientUdpPort = 17478;   // 加入方游戏监听端口

    public volatile bool Running = false;
    public long BytesOut = 0, BytesIn = 0;
    public string LastError = "";
    public bool TcpLocalOk = false, UdpLocalOk = false;

    IPAddress peer;
    TcpListener tcpLocal, tcpPub;
    UdpClient udpLocal, udpPub;
    EndPoint lastGameEp = null;
    EndPoint lastPeerEp = null;
    Thread thTcpPub, thUdpLocal, thUdpPub;

    public void Start(IPAddress peerV6)
    {
        if (Running) return;
        peer = peerV6;
        Running = true;
        LastError = "";
        BytesIn = BytesOut = 0;

        // ---- A. 本地游戏连过来（加入方）----
        try { tcpLocal = new TcpListener(IPAddress.Loopback, TcpPort); tcpLocal.Start(); TcpLocalOk = true; }
        catch (Exception e) { TcpLocalOk = false; LastError = "本地TCP: " + e.Message; }
        if (TcpLocalOk)
        {
            Thread t = new Thread(delegate ()
            {
                while (Running)
                {
                    try
                    {
                        if (!tcpLocal.Pending()) { Thread.Sleep(25); continue; }
                        Socket cli = tcpLocal.AcceptSocket();
                        Thread w = new Thread(delegate () { TcpToPeer(cli); }) { IsBackground = true };
                        w.Start();
                    }
                    catch { Thread.Sleep(40); }
                }
            }) { IsBackground = true };
            t.Start();
        }

        // ---- B. 对方连过来（房主）----
        try
        {
            tcpPub = new TcpListener(IPAddress.IPv6Any, TcpPort);
            tcpPub.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            tcpPub.Start();
            thTcpPub = new Thread(delegate ()
            {
                while (Running)
                {
                    try
                    {
                        if (!tcpPub.Pending()) { Thread.Sleep(25); continue; }
                        Socket remote = tcpPub.AcceptSocket();
                        Thread w = new Thread(delegate () { TcpToGame(remote); }) { IsBackground = true };
                        w.Start();
                    }
                    catch { Thread.Sleep(40); }
                }
            }) { IsBackground = true };
            thTcpPub.Start();
        }
        catch (Exception e) { LastError = "公网TCP: " + e.Message; }

        // ---- C. 本地游戏发的 UDP ----
        try
        {
            udpLocal = new UdpClient(new IPEndPoint(IPAddress.Loopback, UdpPort));
            udpLocal.Client.ReceiveTimeout = 400;
            UdpLocalOk = true;
            thUdpLocal = new Thread(UdpFromGame) { IsBackground = true };
            thUdpLocal.Start();
        }
        catch (Exception e) { UdpLocalOk = false; LastError = "本地UDP: " + e.Message; }

        // ---- D. 对方发来的 UDP ----
        try
        {
            udpPub = new UdpClient(AddressFamily.InterNetworkV6);
            udpPub.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            udpPub.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, UdpPort));
            udpPub.Client.ReceiveTimeout = 400;
            thUdpPub = new Thread(UdpFromPeer) { IsBackground = true };
            thUdpPub.Start();
        }
        catch (Exception e) { LastError = "公网UDP: " + e.Message; }
    }

    public void Stop()
    {
        Running = false;
        try { if (tcpLocal != null) tcpLocal.Stop(); } catch { }
        try { if (tcpPub != null) tcpPub.Stop(); } catch { }
        try { if (udpLocal != null) udpLocal.Close(); } catch { }
        try { if (udpPub != null) udpPub.Close(); } catch { }
        tcpLocal = tcpPub = null; udpLocal = udpPub = null;
        lastGameEp = lastPeerEp = null;
    }

    void TcpToPeer(Socket cli)
    {
        Socket srv = null;
        try
        {
            srv = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            srv.Connect(new IPEndPoint(peer, TcpPort));
            cli.NoDelay = true; srv.NoDelay = true;
            Thread a = new Thread(delegate () { Copy(cli, srv, true); }) { IsBackground = true };
            Thread b = new Thread(delegate () { Copy(srv, cli, false); }) { IsBackground = true };
            a.Start(); b.Start(); a.Join(); b.Join();
        }
        catch (Exception e) { LastError = "转发到对方: " + e.Message; }
        finally { try { cli.Close(); } catch { } try { if (srv != null) srv.Close(); } catch { } }
    }

    void TcpToGame(Socket remote)
    {
        Socket game = null;
        try
        {
            game = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            game.Connect(new IPEndPoint(IPAddress.Loopback, TcpPort));
            game.NoDelay = true; remote.NoDelay = true;
            Thread a = new Thread(delegate () { Copy(remote, game, false); }) { IsBackground = true };
            Thread b = new Thread(delegate () { Copy(game, remote, true); }) { IsBackground = true };
            a.Start(); b.Start(); a.Join(); b.Join();
        }
        catch (Exception e) { LastError = "转发给游戏: " + e.Message; }
        finally { try { remote.Close(); } catch { } try { if (game != null) game.Close(); } catch { } }
    }

    void Copy(Socket from, Socket to, bool outward)
    {
        byte[] buf = new byte[16384];
        try
        {
            while (Running)
            {
                int n = from.Receive(buf);
                if (n <= 0) break;
                to.Send(buf, n, SocketFlags.None);
                if (outward) Interlocked.Add(ref BytesOut, n); else Interlocked.Add(ref BytesIn, n);
            }
        }
        catch { }
    }

    void UdpFromGame()
    {
        IPEndPoint peerEp = new IPEndPoint(peer, UdpPort);
        while (Running)
        {
            try
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = udpLocal.Receive(ref from);
                if (d == null || d.Length == 0) continue;
                lastGameEp = from;
                try
                {
                    if (udpPub != null) { udpPub.Client.SendTo(d, peerEp); Interlocked.Add(ref BytesOut, d.Length); }
                }
                catch { }
            }
            catch { Thread.Sleep(20); }
        }
    }

    void UdpFromPeer()
    {
        while (Running)
        {
            try
            {
                IPEndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);
                byte[] d = udpPub.Receive(ref from);
                if (d == null || d.Length == 0) continue;
                lastPeerEp = from;
                Interlocked.Add(ref BytesIn, d.Length);

                // 优先回给游戏真实来源；否则猜 17478
                IPEndPoint target = lastGameEp as IPEndPoint;
                if (target == null) target = new IPEndPoint(IPAddress.Loopback, ClientUdpPort);
                try
                {
                    if (udpLocal != null) udpLocal.Send(d, d.Length, target);
                    else
                    {
                        using (UdpClient tmp = new UdpClient())
                            tmp.Send(d, d.Length, new IPEndPoint(IPAddress.Loopback, ClientUdpPort));
                    }
                }
                catch { }
            }
            catch { Thread.Sleep(20); }
        }
    }
}
