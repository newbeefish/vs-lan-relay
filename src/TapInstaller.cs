using System;
using System.Runtime.InteropServices;

/// <summary>用 SetupAPI 程序化创建 ROOT 枚举的 TAP 网卡设备，并安装驱动。</summary>
public class TapInstaller
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName,
        ref Guid classGuid, string deviceDescription, IntPtr hwndParent, int creationFlags, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        int property, byte[] propertyBuffer, int propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr deviceInfoSet, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId,
        string fullInfPath, int installFlags, out bool bRebootRequired);

    const int DICD_GENERATE_ID = 0x00000001;
    const int SPDRP_HARDWAREID = 0x00000001;
    const int DIF_REGISTERDEVICE = 0x00000019;
    const int INSTALLFLAG_FORCE = 0x00000001;

    static Guid NetClass = new Guid("4d36e972-e325-11ce-bfc1-08002be10318");

    /// <summary>创建设备并安装驱动。返回空字符串表示成功。日志通过 log 回调输出。</summary>
    public static string Install(string infPath, string hardwareId, string description, Action<string> log)
    {
        if (!System.IO.File.Exists(infPath)) return "找不到 INF 文件: " + infPath;

        IntPtr set = SetupDiCreateDeviceInfoList(ref NetClass, IntPtr.Zero);
        if (set == IntPtr.Zero || set.ToInt64() == -1)
            return "创建设备列表失败: " + Marshal.GetLastWin32Error();
        if (log != null) log("设备列表已创建");

        IntPtr pData = IntPtr.Zero;
        try
        {
            SP_DEVINFO_DATA did = new SP_DEVINFO_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            pData = Marshal.AllocHGlobal(did.cbSize);
            Marshal.StructureToPtr(did, pData, false);

            Guid cls = NetClass;
            if (!SetupDiCreateDeviceInfo(set, "tap0901", ref cls, description, IntPtr.Zero, DICD_GENERATE_ID, pData))
                return "创建设备信息失败: " + Marshal.GetLastWin32Error();
            if (log != null) log("设备信息已创建");

            byte[] hwid = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, pData, SPDRP_HARDWAREID, hwid, hwid.Length))
                return "设置硬件ID失败: " + Marshal.GetLastWin32Error();
            if (log != null) log("硬件 ID 已设置");

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, pData))
                return "注册设备失败: " + Marshal.GetLastWin32Error();
            if (log != null) log("设备已注册");
        }
        finally
        {
            if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            SetupDiDestroyDeviceInfoList(set);
        }

        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out reboot))
        {
            int e = Marshal.GetLastWin32Error();
            if (log != null) log("UpdateDriver 返回错误 " + e);
            if (e != 259 && e != 5) return "安装驱动失败: " + e;
        }
        if (log != null) log("驱动绑定完成");
        return "";
    }
}
