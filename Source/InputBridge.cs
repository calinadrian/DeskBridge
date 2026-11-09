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
    Point previousLocal;
    Rectangle activeLocalBounds;
    ScreenLayout? activeRoute;
    double remoteX,remoteY;
    Size remoteSize=new(1920,1080);
    bool remoteReady, armed, remoteArmed, remoteFullscreen;
    long cooldown;
    public bool Active { get; private set; }
    public ScreenLayout Layout { get; set; }=new();
    public bool PauseFullscreen { get; set; }=true;
    public bool RelativeMouse { get; set; }
    public bool LockLocal { get; set; }
    public int RemoteProtocol { get; private set; }
    public Size RemoteSize => remoteSize;
    public Rectangle[] RemoteScreens => Layout.RemoteScreens;
    public bool RemoteReady => remoteReady;
    bool RelativeActive => RelativeMouse && RemoteProtocol>=2;
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
        Native.GetCursorPos(out var initial); previousLocal=new Point(initial.X,initial.Y);
        guard.Tick+=(_,_) => { if(Active && (Connection?.Alive!=true || (activeRoute ?? Layout).LocalBounds!=activeLocalBounds)) ReturnLocal(); };
        guard.Start();
    }
    public void SetRemoteSize(int width,int height,int protocol=0)
    {
        if(width<100 || height<100 || width>100000 || height>100000) return;
        if(remoteReady && remoteSize!=new Size(width,height)) { ReturnLocal(); Layout=Layout with { RemoteScreens=Array.Empty<Rectangle>() }; }
        remoteSize=new Size(width,height); remoteReady=true; RemoteProtocol=protocol;
    }
    public void Reset() { ReturnLocal(); remoteReady=false; Connection=null; }
    public void SetRemoteScreens(Rectangle[] screens)
    {
        if(screens.SequenceEqual(Layout.RemoteScreens)) return;
        ReturnLocal(); Layout=Layout with { RemoteScreens=screens };
    }
    public void SetRemoteCursor(int x,int y,bool fullscreen)
    {
        if(!Active || RemoteProtocol<3) return;
        remoteX=Math.Clamp(x,0,65535)/65535.0*(remoteSize.Width-1);
        remoteY=Math.Clamp(y,0,65535)/65535.0*(remoteSize.Height-1);
        remoteFullscreen=fullscreen;
    }
    public void Toggle() { if(Active) ReturnLocal(); else EnterRemote(false); }
    void EnterRemote(bool edge,Point? edgePoint=null)
    {
        if(LockLocal || !remoteReady || Connection?.Alive!=true || physicalButtons.Count!=0) return;
        if(RelativeMouse && RemoteProtocol<2) return;
        if(edge && (RelativeActive || (PauseFullscreen && Native.FullscreenForeground()))) return;
        Native.GetCursorPos(out var current);
        var entry=edgePoint ?? new Point(current.X,current.Y);
        var route=edge ? Layout.Routes(remoteSize).FirstOrDefault(r=>r.AtJoiningEdge(entry,remoteSize)) : Layout.Routes(remoteSize).OrderByDescending(r=>r.RemoteMonitor==Layout.RemoteMonitor).FirstOrDefault();
        if(route==null) return;
        activeRoute=route;
        var remote=route.RemoteBounds(remoteSize);
        var selected=route.SelectedRemote(remoteSize);
        remoteX=edge ? Math.Clamp(entry.X-remote.Left,selected.Left+1,selected.Right-2) : selected.Left+selected.Width/2;
        remoteY=edge ? Math.Clamp(entry.Y-remote.Top,selected.Top+1,selected.Bottom-2) : selected.Top+selected.Height/2;
        var screen=Screen.FromPoint(new Point(current.X,current.Y)).Bounds;
        anchor=new Native.POINT { X=screen.Left+screen.Width/2, Y=screen.Top+screen.Height/2 };
        Native.SetCursorPos(anchor.X,anchor.Y);
        // Release keys that Windows already saw locally before interception began.
        foreach(var key in physicalKeys) { try { Native.Key(key,0,true,false); } catch { } }
        forwardedKeys.Clear();
        activeLocalBounds=route.LocalBounds; Active=true; remoteArmed=true;
        if(!Connection.Send(new Packet(Kind.Start,C:3))) { ReturnLocal(); return; }
        if(!RelativeActive) SendPosition();
        Changed?.Invoke(true);
    }
    public void ReturnLocal(ScreenLayout? crossingRoute=null)
    {
        if(!Active) return;
        Active=false; forwardedKeys.Clear();
        Connection?.Send(new Packet(Kind.Release));
        var route=crossingRoute ?? Layout.Routes(remoteSize).FirstOrDefault(r=>r.SelectedRemote(remoteSize).Contains((int)remoteX,(int)remoteY)) ?? activeRoute ?? Layout;
        var point=route.ReturnPoint(remoteX,remoteY,remoteSize);
        Native.SetCursorPos(point.X,point.Y);
        previousLocal=point;
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
        if(button!=0) {
            if(up) physicalButtons.Remove(button); else physicalButtons.Add(button);
            // Releasing a drag must not immediately transfer control at a clamped edge.
            armed=false; remoteArmed=false;
        }
        if(!Active) {
            if(msg==0x200) {
                var b=Screen.FromPoint(new Point(m.Point.X,m.Point.Y)).Bounds;
                if(physicalButtons.Count==0 && m.Point.X>b.Left+24 && m.Point.X<b.Right-25 && m.Point.Y>b.Top+24 && m.Point.Y<b.Bottom-25) armed=true;
                var current=new Point(m.Point.X,m.Point.Y);
                Point crossing=current;
                bool edge=false;
                foreach(var route in Layout.Routes(remoteSize)) {
                    if(route.TryCrossing(previousLocal,current,remoteSize,out var candidate) || route.AtJoiningEdge(current,remoteSize)) {
                        crossing=route.AtJoiningEdge(candidate,remoteSize) ? candidate : current; edge=true; break;
                    }
                }
                previousLocal=current;
                if(EdgeEnabled && armed && edge && Environment.TickCount64>cooldown && physicalKeys.Count==0 && physicalButtons.Count==0)
                {
                    EnterRemote(true,crossing);
                    if(Active) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(mouseHook,code,message,data);
        }
        if(Connection?.Alive!=true) { ReturnLocal(); return Native.CallNextHookEx(mouseHook,code,message,data); }
        if(msg==0x200) {
            int dx=m.Point.X-anchor.X,dy=m.Point.Y-anchor.Y;
            if(RelativeActive) {
                // Never warp the receiving game's pointer or switch devices at an edge in game mode.
                if(dx!=0 || dy!=0) Connection.Send(new Packet(Kind.Move,Math.Clamp(dx,-32768,32767),Math.Clamp(dy,-32768,32767),1));
                return new IntPtr(1);
            }
            double previousX=remoteX,previousY=remoteY;
            remoteX+=dx; remoteY+=dy;
            var returnRoute=Layout.Routes(remoteSize).FirstOrDefault(r=>r.CanReturn(remoteX,remoteY,previousX,previousY,remoteSize));
            bool crossed=returnRoute!=null;
            if(returnRoute!=null) activeRoute=returnRoute;
            if(crossed && remoteArmed && !(PauseFullscreen && remoteFullscreen) && physicalKeys.Count==0 && physicalButtons.Count==0) {
                ReturnLocal(returnRoute);
            }
            else {
                remoteX=Math.Clamp(remoteX,0,remoteSize.Width-1); remoteY=Math.Clamp(remoteY,0,remoteSize.Height-1);
                var route=Layout.Routes(remoteSize).FirstOrDefault(r=>r.SelectedRemote(remoteSize).Contains((int)remoteX,(int)remoteY)) ?? activeRoute ?? Layout;
                var selected=route.SelectedRemote(remoteSize);
                bool away=route.Side switch {
                    ScreenSide.Right=>remoteX>selected.Left+24, ScreenSide.Left=>remoteX<selected.Right-25,
                    ScreenSide.Above=>remoteY<selected.Bottom-25, _=>remoteY>selected.Top+24
                };
                if(physicalButtons.Count==0 && away) remoteArmed=true;
                // New peers report their real cursor position. Never overwrite a game's cursor
                // restoration after a held button with our old absolute position.
                if(RemoteProtocol>=3) {
                    if(dx!=0 || dy!=0) Connection.Send(new Packet(Kind.Move,Math.Clamp(dx,-32768,32767),Math.Clamp(dy,-32768,32767),1));
                } else SendPosition();
            }
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
    bool active,closed,feedback;
    long lastFeedback;
    long lastFullscreenCheck;
    bool fullscreen;
    public event Action<Packet>? CursorChanged;
    public void Handle(Packet p)
    {
        lock(gate) {
            if(closed) return;
            if(p.Kind==Kind.Release) { Release(); return; }
            if(p.Kind==Kind.Start) { Release(); active=true; feedback=p.C>=3; return; }
            if(!active) return;
            switch(p.Kind) {
                case Kind.Move:
                    if(p.C==1) Native.Mouse(Math.Clamp(p.A,-32768,32767),Math.Clamp(p.B,-32768,32767),0,0x2001);
                    else Native.Mouse(Math.Clamp(p.A,0,65535),Math.Clamp(p.B,0,65535),0,0xC001);
                    ReportCursor(p.C!=1);
                    break;
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
                    ReportCursor(true);
                    break;
                case Kind.Wheel:
                    Native.Mouse(0,0,unchecked((uint)Math.Clamp(p.A,-32768,32767)),p.B==1 ? 0x1000u : 0x800u); break;
            }
        }
    }
    void ReportCursor(bool force)
    {
        if(!feedback || (!force && Environment.TickCount64-lastFeedback<20)) return;
        if(!Native.GetCursorPos(out var p)) return;
        lastFeedback=Environment.TickCount64;
        if(Environment.TickCount64-lastFullscreenCheck>200) {
            fullscreen=Native.FullscreenForeground(); lastFullscreenCheck=Environment.TickCount64;
        }
        var b=SystemInformation.VirtualScreen;
        CursorChanged?.Invoke(new Packet(Kind.Cursor,
            (int)Math.Round(Math.Clamp((p.X-b.Left)/(double)Math.Max(1,b.Width-1),0,1)*65535),
            (int)Math.Round(Math.Clamp((p.Y-b.Top)/(double)Math.Max(1,b.Height-1),0,1)*65535),D:fullscreen ? 1 : 0));
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
