using System.Runtime.InteropServices;
using System.ComponentModel;

namespace DeskBridge;

internal static class Native
{
    internal delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public POINT Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError=true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out RECT rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int maxCount);
    internal static bool FullscreenForeground()
    {
        var window=GetForegroundWindow();
        if(window==IntPtr.Zero || !GetWindowRect(window,out var r)) return false;
        var name=new System.Text.StringBuilder(256); GetClassName(window,name,name.Capacity);
        if(name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var b=Screen.FromHandle(window).Bounds;
        return r.Left<=b.Left+2 && r.Top<=b.Top+2 && r.Right>=b.Right-2 && r.Bottom>=b.Bottom-2;
    }
    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    internal static void Key(int key, int scan, bool up, bool extended)
    {
        var value = new INPUT { Type=1, Value=new InputUnion { Keyboard=new KEYBDINPUT {
            Key=(ushort)(scan == 0 ? key : 0), Scan=(ushort)scan,
            Flags=(uint)((scan != 0 ? 8 : 0) | (up ? 2 : 0) | (extended ? 1 : 0)) } } };
        Send(value);
    }
    internal static void Mouse(int x, int y, uint data, uint flags) => Send(new INPUT {
        Type=0, Value=new InputUnion { Mouse=new MOUSEINPUT { X=x, Y=y, Data=data, Flags=flags } } });
    static void Send(INPUT input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked input. Check whether the target app is running as administrator.");
    }
}
