using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBridge;

internal enum ScreenSide { Right, Left, Above, Below }

internal sealed record ScreenLayout
{
    public ScreenSide Side { get; init; }=ScreenSide.Right;
    public int OffsetPercent { get; init; }
    public string Monitor { get; init; }="";
    [JsonIgnore]
    public Rectangle LocalBounds => (Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==Monitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
    public Rectangle RemoteBounds(Size size)
    {
        var b=LocalBounds;
        bool horizontal=Side is ScreenSide.Right or ScreenSide.Left;
        int localLength=horizontal ? b.Height : b.Width, remoteLength=horizontal ? size.Height : size.Width;
        int offset=(int)Math.Round((localLength-remoteLength)/2.0+Math.Clamp(OffsetPercent,-95,95)/100.0*(localLength+remoteLength)/2.0);
        offset=Math.Clamp(offset,-remoteLength+32,localLength-32);
        return Side switch {
            ScreenSide.Left=>new Rectangle(b.Left-size.Width,b.Top+offset,size.Width,size.Height),
            ScreenSide.Above=>new Rectangle(b.Left+offset,b.Top-size.Height,size.Width,size.Height),
            ScreenSide.Below=>new Rectangle(b.Left+offset,b.Bottom,size.Width,size.Height),
            _=>new Rectangle(b.Right,b.Top+offset,size.Width,size.Height)
        };
    }
    public bool AtJoiningEdge(Point p,Size remoteSize)
    {
        var b=LocalBounds; var r=RemoteBounds(remoteSize);
        return Side switch {
            ScreenSide.Left=>p.X<=b.Left && p.X>=b.Left-1 && p.Y>=Math.Max(b.Top,r.Top) && p.Y<Math.Min(b.Bottom,r.Bottom),
            ScreenSide.Right=>p.X>=b.Right-1 && p.X<=b.Right && p.Y>=Math.Max(b.Top,r.Top) && p.Y<Math.Min(b.Bottom,r.Bottom),
            ScreenSide.Above=>p.Y<=b.Top && p.Y>=b.Top-1 && p.X>=Math.Max(b.Left,r.Left) && p.X<Math.Min(b.Right,r.Right),
            _=>p.Y>=b.Bottom-1 && p.Y<=b.Bottom && p.X>=Math.Max(b.Left,r.Left) && p.X<Math.Min(b.Right,r.Right)
        };
    }
    public bool CanReturn(double x,double y,Size remoteSize)
    {
        var b=LocalBounds; var r=RemoteBounds(remoteSize);
        return Side switch {
            ScreenSide.Right=>x<0 && r.Top+y>=b.Top && r.Top+y<b.Bottom,
            ScreenSide.Left=>x>=remoteSize.Width && r.Top+y>=b.Top && r.Top+y<b.Bottom,
            ScreenSide.Above=>y>=remoteSize.Height && r.Left+x>=b.Left && r.Left+x<b.Right,
            _=>y<0 && r.Left+x>=b.Left && r.Left+x<b.Right
        };
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
    public override string ToString() => $"Legion Go: {Side.ToString().ToLowerInvariant()} · alignment {OffsetPercent:+0;-0;0}%";
}

internal sealed record Preferences
{
    public ScreenLayout Layout { get; init; }=new();
    public bool EdgeEnabled { get; init; }=true;
    public bool PauseFullscreen { get; init; }=true;
    public bool RelativeMouse { get; init; }
    public bool LockLocal { get; init; }
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
