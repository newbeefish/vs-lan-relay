using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class KeyInject
{
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const uint KEYUP = 0x0002;

    public static void Send(Keys k, bool up)
    {
        try { keybd_event((byte)((int)k & 0xFF), 0, up ? KEYUP : 0, UIntPtr.Zero); }
        catch { }
    }
}