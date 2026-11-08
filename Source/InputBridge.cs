using System.Runtime.InteropServices;
using System.ComponentModel;

namespace DeskBridge;

internal sealed class InputCapture : IDisposable
{
    readonly Native.Hook mouseProc, keyProc;
    readonly IntPtr mouseHook, keyHook;
    readonly HashSet<int> physicalKeys=new(), forwardedKeys=new(), swallowed=new();
    readonly HashSet<int> physicalButtons=new();
    readonly System.Windows.Forms.Timer guard=new() { Interval=100 };
    Native.POINT anchor;
    double remoteX,remoteY;
    Size remoteSize=new(1920,1080);
    bool remoteReady, armed;
    long cooldown;
    public bool Active { get; private set; }
    public bool RightSide { get; set; }=true;
    public bool EdgeEnabled { get; set; }=true;
    public Connection? Connection { get; set; }
    public event Action<bool>? Changed;

    public InputCapture()
    {
        mouseProc=Mouse; keyProc=Keyboard;
        mouseHook=Native.SetWindowsHookEx(14,mouseProc,Native.GetModuleHandle(null),0);
        keyHook=Native.SetWindowsHookEx(13,keyProc,Native.GetModuleHandle(null),0);
        if(mouseHook==IntPtr.Zero || keyHook==IntPtr.Zero) {
            Native.UnhookWindowsHookEx(mouseHook); Native.UnhookWindowsHookEx(keyHook);
            throw new Win32Exception(Marshal.GetLastWin32Error(),"Unable to capture keyboard and mouse.");
        }
        guard.Tick+=(_,_) => { if(Active && Connection?.Alive!=true) ReturnLocal(); };
        guard.Start();
    }
    public void SetRemoteSize(int width,int height)
    {
        if(width<100 || height<100 || width>100000 || height>100000) return;
        remoteSize=new Size(width,height); remoteReady=true;
    }
    public void Reset() { ReturnLocal(); remoteReady=false; Connection=null; }
    public void Toggle() { if(Active) ReturnLocal(); else EnterRemote(false); }
    void EnterRemote(bool edge)
    {
        if(!remoteReady || Connection?.Alive!=true || physicalButtons.Count!=0) return;
        var bounds=SystemInformation.VirtualScreen;
        Native.GetCursorPos(out var current);
        remoteY=Math.Clamp((current.Y-bounds.Top)/(double)Math.Max(1,bounds.Height-1)*(remoteSize.Height-1),0,remoteSize.Height-1);
        remoteX=edge ? (RightSide ? 1 : remoteSize.Width-2) : remoteSize.Width/2;
        var screen=Screen.FromPoint(new Point(current.X,current.Y)).Bounds;
        anchor=new Native.POINT { X=screen.Left+screen.Width/2, Y=screen.Top+screen.Height/2 };
        Native.SetCursorPos(anchor.X,anchor.Y);
        // Release keys that Windows already saw locally before interception began.
        foreach(var key in physicalKeys) { try { Native.Key(key,0,true,false); } catch { } }
        forwardedKeys.Clear();
        Active=true;
        if(!Connection.Send(new Packet(Kind.Start))) { ReturnLocal(); return; }
        SendPosition(); Changed?.Invoke(true);
    }
    public void ReturnLocal()
    {
        if(!Active) return;
        Active=false; forwardedKeys.Clear();
        Connection?.Send(new Packet(Kind.Release));
        var b=SystemInformation.VirtualScreen;
        Native.SetCursorPos(RightSide ? b.Right-12 : b.Left+12,
            b.Top+(int)(Math.Clamp(remoteY/(remoteSize.Height-1),0,1)*(b.Height-1)));
        armed=false; cooldown=Environment.TickCount64+700;
        Changed?.Invoke(false);
    }
    void SendPosition() => Connection?.Send(new Packet(Kind.Move,
        (int)Math.Round(Math.Clamp(remoteX/(remoteSize.Width-1),0,1)*65535),
        (int)Math.Round(Math.Clamp(remoteY/(remoteSize.Height-1),0,1)*65535)));
    IntPtr Mouse(int code,IntPtr message,IntPtr data)
    {
        if(code<0) return Native.CallNextHookEx(mouseHook,code,message,data);
        var m=Marshal.PtrToStructure<Native.MouseData>(data);
        if((m.Flags&1)!=0) return Native.CallNextHookEx(mouseHook,code,message,data);
        int msg=message.ToInt32();
        int button=msg switch { 0x201 or 0x202=>1,0x204 or 0x205=>2,0x207 or 0x208=>3,
            0x20B or 0x20C=>((m.Data>>16)==1 ? 4 : 5),_=>0 };
        bool up=msg is 0x202 or 0x205 or 0x208 or 0x20C;
        if(button!=0) { if(up) physicalButtons.Remove(button); else physicalButtons.Add(button); }
        if(!Active) {
            if(msg==0x200) {
                var b=SystemInformation.VirtualScreen;
                if(m.Point.X>b.Left+24 && m.Point.X<b.Right-25) armed=true;
                bool edge=RightSide ? m.Point.X>=b.Right-1 : m.Point.X<=b.Left;
                if(EdgeEnabled && armed && edge && Environment.TickCount64>cooldown && physicalKeys.Count==0 && physicalButtons.Count==0)
                {
                    EnterRemote(true);
                    if(Active) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(mouseHook,code,message,data);
        }
        if(Connection?.Alive!=true) { ReturnLocal(); return Native.CallNextHookEx(mouseHook,code,message,data); }
        if(msg==0x200) {
            int dx=m.Point.X-anchor.X,dy=m.Point.Y-anchor.Y;
            remoteX+=dx; remoteY=Math.Clamp(remoteY+dy,0,remoteSize.Height-1);
            bool crossed=RightSide ? remoteX<0 : remoteX>=remoteSize.Width;
            if(crossed && physicalKeys.Count==0 && physicalButtons.Count==0) ReturnLocal();
            else { remoteX=Math.Clamp(remoteX,0,remoteSize.Width-1); SendPosition(); }
        } else if(button!=0) Connection.Send(new Packet(Kind.Button,button,up ? 1 : 0));
        else if(msg is 0x20A or 0x20E) Connection.Send(new Packet(Kind.Wheel,unchecked((short)(m.Data>>16)),msg==0x20E ? 1 : 0));
        return new IntPtr(1);
    }
    IntPtr Keyboard(int code,IntPtr message,IntPtr data)
    {
        if(code<0) return Native.CallNextHookEx(keyHook,code,message,data);
        var k=Marshal.PtrToStructure<Native.KeyData>(data);
        if((k.Flags&0x10)!=0) return Native.CallNextHookEx(keyHook,code,message,data);
        int key=(int)k.Key;
        bool up=(k.Flags&0x80)!=0;
        bool first=up ? false : physicalKeys.Add(key);
        if(up) physicalKeys.Remove(key);
        bool ctrl=physicalKeys.Contains(0xA2)||physicalKeys.Contains(0xA3)||physicalKeys.Contains(0x11);
        bool alt=physicalKeys.Contains(0xA4)||physicalKeys.Contains(0xA5)||physicalKeys.Contains(0x12);
        if(!up && ctrl && alt && key is 0x20 or 0x1B) {
            swallowed.Add(key);
            if(first) { if(key==0x1B) ReturnLocal(); else Toggle(); }
            return new IntPtr(1);
        }
        if(swallowed.Contains(key)) { if(up) swallowed.Remove(key); return new IntPtr(1); }
        if(!Active) return Native.CallNextHookEx(keyHook,code,message,data);
        if(up) {
            if(forwardedKeys.Remove(key)) Connection?.Send(new Packet(Kind.Key,key,(int)k.Scan,1,(int)(k.Flags&1)));
        } else {
            // Keys held before switching need to be released before they can be shared.
            if(first || forwardedKeys.Contains(key)) {
                forwardedKeys.Add(key);
                Connection?.Send(new Packet(Kind.Key,key,(int)k.Scan,0,(int)(k.Flags&1)));
            }
        }
        return new IntPtr(1);
    }
    public void Dispose() { ReturnLocal(); guard.Dispose(); Native.UnhookWindowsHookEx(mouseHook); Native.UnhookWindowsHookEx(keyHook); }
}

internal sealed class InputSink
{
    readonly object gate=new();
    readonly Dictionary<int,Packet> keys=new();
    readonly HashSet<int> buttons=new();
    bool active,closed;
    public void Handle(Packet p)
    {
        lock(gate) {
            if(closed) return;
            if(p.Kind==Kind.Release) { Release(); return; }
            if(p.Kind==Kind.Start) { Release(); active=true; return; }
            if(!active) return;
            switch(p.Kind) {
                case Kind.Move:
                    Native.Mouse(Math.Clamp(p.A,0,65535),Math.Clamp(p.B,0,65535),0,0xC001); break;
                case Kind.Key:
                    if(p.A<1 || p.A>255 || p.B<0 || p.B>255 || p.C is <0 or >1 || p.D is <0 or >1) return;
                    if(p.C==0) keys[p.A]=p;
                    Native.Key(p.A,p.B,p.C!=0,p.D!=0);
                    if(p.C!=0) keys.Remove(p.A);
                    break;
                case Kind.Button:
                    if(p.A<1 || p.A>5 || p.B is <0 or >1) return;
                    if(p.B==0) buttons.Add(p.A);
                    Button(p.A,p.B!=0);
                    if(p.B!=0) buttons.Remove(p.A);
                    break;
                case Kind.Wheel:
                    Native.Mouse(0,0,unchecked((uint)Math.Clamp(p.A,-32768,32767)),p.B==1 ? 0x1000u : 0x800u); break;
            }
        }
    }
    static void Button(int button,bool up)
    {
        uint flags=button switch { 1=>up ? 4u : 2u,2=>up ? 16u : 8u,3=>up ? 64u : 32u,_=>up ? 256u : 128u };
        Native.Mouse(0,0,button>=4 ? (uint)(button-3) : 0,flags);
    }
    void Release()
    {
        active=false;
        foreach(var k in keys.Values) { try { Native.Key(k.A,k.B,true,k.D!=0); } catch { } }
        foreach(var b in buttons) { try { Button(b,true); } catch { } }
        keys.Clear(); buttons.Clear();
    }
    public void Close() { lock(gate) { closed=true; Release(); } }
}
