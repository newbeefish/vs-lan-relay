using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

// 原始套接字底层：抓包（SIO_RCVALL）+ 伪造投递（IP_HDRINCL）
// 目的：完全绕开“游戏选了哪块网卡”这个问题
static class RawNet
{
    public const int SIO_RCVALL = unchecked((int)0x98000001);

    // 在所有非环回、已启用的 IPv4 网卡上开抓包
    public static List<Socket> OpenCapture()
    {
        var list = new List<Socket>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    try
                    {
                        Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                        s.Bind(new IPEndPoint(ua.Address, 0));
                        s.IOControl(SIO_RCVALL, BitConverter.GetBytes(1), null);
                        s.ReceiveTimeout = 500;
                        s.ReceiveBufferSize = 1024 * 1024;
                        list.Add(s);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return list;
    }

    // 伪造投递用的 socket
    public static Socket OpenInject()
    {
        Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
        return s;
    }

    // 从原始 IP 包中解析 UDP
    public static bool TryParseUdp(byte[] b, int len,
        out IPAddress src, out IPAddress dst,
        out int sp, out int dp, out int payloadOff, out int payloadLen)
    {
        src = null; dst = null; sp = 0; dp = 0; payloadOff = 0; payloadLen = 0;
        if (len < 28) return false;
        if ((b[0] >> 4) != 4) return false;
        int ihl = (b[0] & 0x0F) * 4;
        if (ihl < 20 || len < ihl + 8) return false;
        if (b[9] != 17) return false;                       // 只处理 UDP
        int tot = (b[2] << 8) | b[3];
        if (tot <= 0 || tot > len) tot = len;
        src = new IPAddress(new byte[] { b[12], b[13], b[14], b[15] });
        dst = new IPAddress(new byte[] { b[16], b[17], b[18], b[19] });
        sp = (b[ihl] << 8) | b[ihl + 1];
        dp = (b[ihl + 2] << 8) | b[ihl + 3];
        payloadOff = ihl + 8;
        payloadLen = tot - payloadOff;
        if (payloadLen < 0) payloadLen = 0;
        return true;
    }

    // 构造完整 IP+UDP 包（含校验和）
    public static byte[] BuildUdp(IPAddress srcAddr, int sp, IPAddress dstAddr, int dp, byte[] payload)
    {
        int udpLen = 8 + payload.Length;
        int totLen = 20 + udpLen;
        byte[] p = new byte[totLen];

        p[0] = 0x45; p[1] = 0x00;
        p[2] = (byte)(totLen >> 8); p[3] = (byte)totLen;
        p[4] = 0x12; p[5] = 0x34;                            // 识别号
        p[6] = 0x40; p[7] = 0x00;
        p[8] = 64; p[9] = 17;
        byte[] sa = srcAddr.GetAddressBytes();
        byte[] da = dstAddr.GetAddressBytes();
        Array.Copy(sa, 0, p, 12, 4);
        Array.Copy(da, 0, p, 16, 4);

        // IP 首部校验和
        int sum = 0;
        for (int i = 0; i < 20; i += 2) sum += (p[i] << 8) + p[i + 1];
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        sum = (~sum) & 0xFFFF;
        p[10] = (byte)(sum >> 8); p[11] = (byte)sum;

        // UDP 首部
        p[20] = (byte)(sp >> 8); p[21] = (byte)sp;
        p[22] = (byte)(dp >> 8); p[23] = (byte)dp;
        p[24] = (byte)(udpLen >> 8); p[25] = (byte)udpLen;
        p[26] = 0; p[27] = 0;
        Array.Copy(payload, 0, p, 28, payload.Length);

        // UDP 校验和（含伪首部）
        sum = 0;
        sum += (sa[0] << 8) + sa[1];
        sum += (sa[2] << 8) + sa[3];
        sum += (da[0] << 8) + da[1];
        sum += (da[2] << 8) + da[3];
        sum += 17;
        sum += udpLen;
        for (int i = 20; i < totLen; i += 2)
        {
            int hi = p[i];
            int lo = (i + 1 < totLen) ? p[i + 1] : 0;
            sum += (hi << 8) + lo;
        }
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        sum = (~sum) & 0xFFFF;
        if (sum == 0) sum = 0xFFFF;
        p[26] = (byte)(sum >> 8); p[27] = (byte)sum;

        return p;
    }
}
