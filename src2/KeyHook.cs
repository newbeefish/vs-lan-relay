using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// ============================================================
//  全局键盘钩子
//   · 1~6        → 表情
//   · 自定义键位 → 转译成默认键后注入给游戏
// ============================================================
public class KeyHook
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    LowLevelKeyboardProc proc;
    IntPtr hookId = IntPtr.Zero;

    public bool RemapEnabled = false;
    public Keys[] RemapFrom;      // 用户设置的键（与 Actions 对应）
    public Keys[] RemapTo;        // 默认键

    public event Action<int> OnEmojiKey;   // 0~5

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    // 修饰键轮询状态
    static readonly Keys[] Mods = { Keys.ShiftKey, Keys.ControlKey, Keys.Menu };
    static readonly bool[] modDown = new bool[3];

    // 每帧调用：返回需要注入的 (目标键, 是否抬起)
    public Action<Keys, bool> OnModifierAction;

    Thread pollThread = null;

    // 独立高频线程轮询修饰键（8ms 一次，能抓到短按）
    public void StartModifierPolling()
    {
        if (pollThread != null) return;
        pollThread = new Thread(delegate ()
        {
            while (true)
            {
                Thread.Sleep(8);
                try { PollModifiers(); } catch { }
            }
        });
        pollThread.IsBackground = true;
        pollThread.Start();
    }

    public void PollModifiers()
    {
        if (!RemapEnabled || RemapFrom == null || RemapTo == null) return;

        for (int i = 0; i < Mods.Length; i++)
        {
            Keys mod = Mods[i];
            int vk = (int)mod;
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;

            // 只在配置里用到这个修饰键时才处理
            int idx = Array.IndexOf(RemapFrom, mod);
            if (idx < 0 || RemapTo[idx] == mod) { modDown[i] = down; continue; }

            if (down != modDown[i])
            {
                modDown[i] = down;
                Action<Keys, bool> h = OnModifierAction;
                if (h != null) h(RemapTo[idx], !down);
            }
        }
    }

    public bool Installed { get { return hookId != IntPtr.Zero; } }

    public void Install()
    {
        proc = HookCallback;
        using (System.Diagnostics.Process cur = System.Diagnostics.Process.GetCurrentProcess())
        using (System.Diagnostics.ProcessModule mod = cur.MainModule)
            hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(mod.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (hookId != IntPtr.Zero) { UnhookWindowsHookEx(hookId); hookId = IntPtr.Zero; }
    }

    // 注入一个按键（不拦截）
    static void Inject(Keys k, bool up)
    {
        byte vk = (byte)((int)k & 0xFF);
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        keybd_event(vk, 0, flags, UIntPtr.Zero);
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            KBDLLHOOKSTRUCT info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            // LLKHF_INJECTED(0x10)：我们自己注入的键，直接放过，避免死循环
            bool injected = (info.flags & 0x10) != 0;
            bool down = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
            bool up = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);
            if (injected) return CallNextHookEx(hookId, nCode, wParam, lParam);

            if (down || up)
            {
                Keys k = (Keys)info.vkCode;

                // 表情键：主键盘 1~6（非小键盘）
                if (down && k >= Keys.D1 && k <= Keys.D6 && (info.flags & 0x01000000) == 0)
                {
                    Action<int> h = OnEmojiKey;
                    if (h != null) { try { h((int)k - (int)Keys.D1); } catch { } }
                }

                // 键位转译
                if (RemapEnabled && RemapFrom != null && RemapTo != null)
                {
                    int idx = Array.IndexOf(RemapFrom, k);
                    if (idx >= 0 && RemapTo[idx] != k)
                    {
                        Keys dest = RemapTo[idx];
                        bool isUp = up;
                        // 在钩子回调里直接注入会被系统丢弃，改为异步稍后注入
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate
                        {
                            System.Threading.Thread.Sleep(6);
                            Inject(dest, isUp);
                        });
                        return (IntPtr)1;   // 吞掉原键
                    }
                }
            }
        }
        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }
}
