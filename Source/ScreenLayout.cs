using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBridge;

internal enum ScreenSide { Right, Left, Above, Below }

internal sealed record MonitorPlacement(int Index, string LocalMonitor, ScreenSide Side, int OffsetPercent);

internal sealed record ScreenLayout
{
    public MonitorPlacement[] Placements { get; init; }=Array.Empty<MonitorPlacement>();
    public IEnumerable<ScreenLayout> Routes(Size size)
    {
        if(Placements.Length==0 || RemoteScreens.Length==0) { yield return this with { Placements=Array.Empty<MonitorPlacement>() }; yield break; }
        foreach(var placement in Placements) {
            if(placement.Index<0 || placement.Index>=RemoteScreens.Length) continue;
            yield return this with { Placements=Array.Empty<MonitorPlacement>(), RemoteMonitor=placement.Index,
                Independent=true, Monitor=placement.LocalMonitor, Side=placement.Side, OffsetPercent=placement.OffsetPercent };
        }
    }
    public ScreenSide Side { get; init; }=ScreenSide.Right;
    public int OffsetPercent { get; init; }
    public string Monitor { get; init; }="";
    [JsonIgnore] public bool Independent { get; init; }
    public int RemoteMonitor { get; init; }
    public Rectangle[] RemoteScreens { get; init; }=Array.Empty<Rectangle>();
    public Rectangle SelectedRemote(Size size) => RemoteScreens.Length==0 ? new Rectangle(Point.Empty,size) : RemoteScreens[RemoteMonitor>=0 && RemoteMonitor<RemoteScreens.Length ? RemoteMonitor : 0];
    [JsonIgnore]
    public Rectangle LocalBounds => (Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==Monitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
    public Rectangle RemoteBounds(Size size)
    {
        var b=LocalBounds;
        var selected=SelectedRemote(size);
        var monitorSize=selected.Size;
        bool horizontal=Side is ScreenSide.Right or ScreenSide.Left;
        int localLength=horizontal ? b.Height : b.Width, remoteLength=horizontal ? monitorSize.Height : monitorSize.Width;
        int offset=(int)Math.Round((localLength-remoteLength)/2.0+Math.Clamp(OffsetPercent,-95,95)/100.0*(localLength+remoteLength)/2.0);
        offset=Math.Clamp(offset,-remoteLength+32,localLength-32);
        var placed=Side switch {
            ScreenSide.Left=>new Point(b.Left-monitorSize.Width,b.Top+offset),
            ScreenSide.Above=>new Point(b.Left+offset,b.Top-monitorSize.Height),
            ScreenSide.Below=>new Point(b.Left+offset,b.Bottom),
            _=>new Point(b.Right,b.Top+offset)
        };
        return new Rectangle(placed.X-selected.X,placed.Y-selected.Y,size.Width,size.Height);
    }
    public Rectangle PortalBounds(Size size)
    {
        var origin=RemoteBounds(size); var monitor=SelectedRemote(size);
        return new Rectangle(origin.Left+monitor.Left,origin.Top+monitor.Top,monitor.Width,monitor.Height);
    }
    public List<(int Start,int End)> PortalSpans(Size size)
    {
        var local=LocalBounds; var portal=PortalBounds(size); var origin=RemoteBounds(size);
        bool horizontal=Side is ScreenSide.Left or ScreenSide.Right;
        int start=horizontal ? Math.Max(local.Top,portal.Top) : Math.Max(local.Left,portal.Left);
        int end=horizontal ? Math.Min(local.Bottom,portal.Bottom) : Math.Min(local.Right,portal.Right);
        var spans=new List<(int Start,int End)>(); if(end<=start) return spans;
        spans.Add((start,end));
        int line=Side switch { ScreenSide.Right=>portal.Left-1,ScreenSide.Left=>portal.Right,ScreenSide.Above=>portal.Bottom,_=>portal.Top-1 };
        var selected=SelectedRemote(size);
        foreach(var screen in Independent ? Array.Empty<Rectangle>() : RemoteScreens) {
            if(screen==selected) continue;
            var r=new Rectangle(origin.Left+screen.Left,origin.Top+screen.Top,screen.Width,screen.Height);
            if(horizontal ? line<r.Left || line>=r.Right : line<r.Top || line>=r.Bottom) continue;
            int blockStart=horizontal ? r.Top : r.Left,blockEnd=horizontal ? r.Bottom : r.Right;
            var remaining=new List<(int Start,int End)>();
            foreach(var span in spans) {
                if(blockEnd<=span.Start || blockStart>=span.End) remaining.Add(span);
                else {
                    if(blockStart>span.Start) remaining.Add((span.Start,blockStart));
                    if(blockEnd<span.End) remaining.Add((blockEnd,span.End));
                }
            }
            spans=remaining;
        }
        int localLine=Side switch { ScreenSide.Right=>local.Right,ScreenSide.Left=>local.Left-1,ScreenSide.Above=>local.Top-1,_=>local.Bottom };
        foreach(var screen in Screen.AllScreens) {
            var r=screen.Bounds; if(r==local) continue;
            if(horizontal ? localLine<r.Left || localLine>=r.Right : localLine<r.Top || localLine>=r.Bottom) continue;
            int blockStart=horizontal ? r.Top : r.Left,blockEnd=horizontal ? r.Bottom : r.Right;
            var remaining=new List<(int Start,int End)>();
            foreach(var span in spans) {
                if(blockEnd<=span.Start || blockStart>=span.End) remaining.Add(span);
                else {
                    if(blockStart>span.Start) remaining.Add((span.Start,blockStart));
                    if(blockEnd<span.End) remaining.Add((blockEnd,span.End));
                }
            }
            spans=remaining;
        }
        return spans;
    }
    public bool AtJoiningEdge(Point p,Size remoteSize)
    {
        var b=LocalBounds;
        bool onEdge=Side switch {
            ScreenSide.Left=>p.X<=b.Left && p.X>=b.Left-1,
            ScreenSide.Right=>p.X>=b.Right-1 && p.X<=b.Right,
            ScreenSide.Above=>p.Y<=b.Top && p.Y>=b.Top-1,
            _=>p.Y>=b.Bottom-1 && p.Y<=b.Bottom
        };
        if(!onEdge) return false;
        int along=Side is ScreenSide.Left or ScreenSide.Right ? p.Y : p.X;
        return PortalSpans(remoteSize).Any(s=>along>=s.Start && along<s.End);
    }
    public bool CanReturn(double x,double y,double previousX,double previousY,Size remoteSize)
    {
        var m=SelectedRemote(remoteSize); var r=RemoteBounds(remoteSize);
        if(!m.Contains((int)previousX,(int)previousY)) return false;
        bool crossed=Side switch {
            ScreenSide.Right=>x<m.Left, ScreenSide.Left=>x>=m.Right,
            ScreenSide.Above=>y>=m.Bottom, _=>y<m.Top
        };
        if(!crossed) return false;
        double along=Side is ScreenSide.Left or ScreenSide.Right ? r.Top+y : r.Left+x;
        return PortalSpans(remoteSize).Any(s=>along>=s.Start && along<s.End);
    }
    public bool TryCrossing(Point previous,Point current,Size remoteSize,out Point crossing)
    {
        crossing=current; var b=LocalBounds;
        if(!b.Contains(previous)) return false;
        double t;
        if(Side==ScreenSide.Right && current.X>=b.Right-1 && previous.X<b.Right-1) t=(b.Right-1.0-previous.X)/(current.X-previous.X);
        else if(Side==ScreenSide.Left && current.X<=b.Left && previous.X>b.Left) t=(b.Left-(double)previous.X)/(current.X-previous.X);
        else if(Side==ScreenSide.Above && current.Y<=b.Top && previous.Y>b.Top) t=(b.Top-(double)previous.Y)/(current.Y-previous.Y);
        else if(Side==ScreenSide.Below && current.Y>=b.Bottom-1 && previous.Y<b.Bottom-1) t=(b.Bottom-1.0-previous.Y)/(current.Y-previous.Y);
        else return false;
        crossing=new Point((int)Math.Round(previous.X+t*(current.X-previous.X)),(int)Math.Round(previous.Y+t*(current.Y-previous.Y)));
        return AtJoiningEdge(crossing,remoteSize);
    }
    public Point ReturnPoint(double x,double y,Size size)
    {
        var b=LocalBounds; var r=RemoteBounds(size);
        int alongX=(int)Math.Clamp(r.Left+x,b.Left+12,b.Right-13), alongY=(int)Math.Clamp(r.Top+y,b.Top+12,b.Bottom-13);
        return Side switch {
            ScreenSide.Left=>new Point(b.Left+12,alongY), ScreenSide.Right=>new Point(b.Right-13,alongY),
            ScreenSide.Above=>new Point(alongX,b.Top+12), _=>new Point(alongX,b.Bottom-13)
        };
    }
    public override string ToString() => Placements.Length>0 ? $"{Placements.Length} independently arranged remote monitor(s)" : $"Legion Go: {Side.ToString().ToLowerInvariant()} · alignment {OffsetPercent:+0;-0;0}%";
}

internal sealed record Preferences
{
    public ScreenLayout Layout { get; init; }=new();
    public bool EdgeEnabled { get; init; }=true;
    public bool PauseFullscreen { get; init; }=true;
    public bool RelativeMouse { get; init; }
    public bool LockLocal { get; init; }
    public bool ReceiveMode { get; init; }
    public string Address { get; init; }="";
    public bool RememberConnection { get; init; }=true;
    public bool AutoConnect { get; init; }
    public bool StartWithWindows { get; init; }
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeskBridge","settings.json");
    public static Preferences Load()
    {
        try {
            var value=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath));
            if(value?.Layout==null || !Enum.IsDefined(value.Layout.Side)) return new();
            return value with { Layout=value.Layout with { OffsetPercent=Math.Clamp(value.Layout.OffsetPercent,-95,95) } };
        } catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath+".tmp",JsonSerializer.Serialize(this));
        File.Move(FilePath+".tmp",FilePath,true);
    }
}
