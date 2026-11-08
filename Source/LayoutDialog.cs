using System.Drawing.Drawing2D;

namespace DeskBridge;

internal sealed class LayoutDialog : Form
{
    readonly ComboBox monitors=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=330 };
    readonly ComboBox sides=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=120 };
    readonly NumericUpDown offset=new() { Minimum=-95, Maximum=95, Width=80 };
    readonly LayoutCanvas canvas=new() { Dock=DockStyle.Fill };
    readonly Screen[] screens=Screen.AllScreens;
    public ScreenLayout LayoutValue { get; private set; }
    bool updating;
    public LayoutDialog(ScreenLayout layout,Size remoteSize,bool connected)
    {
        LayoutValue=layout;
        Text="Arrange monitors"; ClientSize=new Size(760,580); MinimumSize=new Size(700,540);
        StartPosition=FormStartPosition.CenterParent;
        BackColor=Color.FromArgb(21,26,35); ForeColor=Color.FromArgb(230,236,244); Font=new Font("Segoe UI",10);
        var outer=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(20), ColumnCount=1, RowCount=5 };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent,100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(outer);
        outer.Controls.Add(new Label { Text="Drag the Legion Go to match your desk", AutoSize=true, Font=new Font("Segoe UI",17,FontStyle.Bold) },0,0);
        var fields=new FlowLayoutPanel { AutoSize=true, Dock=DockStyle.Fill, Margin=new Padding(0,12,0,10), WrapContents=true };
        fields.Controls.Add(new Label { Text="Connect to local monitor", AutoSize=true, Padding=new Padding(0,5,0,0) });
        for(int i=0;i<screens.Length;i++) monitors.Items.Add($"{i+1}: {screens[i].Bounds.Width} × {screens[i].Bounds.Height}"+(screens[i].Primary ? " (primary)" : ""));
        int index=Array.FindIndex(screens,s=>s.DeviceName==layout.Monitor);
        monitors.SelectedIndex=index>=0 ? index : Math.Max(0,Array.FindIndex(screens,s=>s.Primary));
        fields.Controls.Add(monitors);
        fields.SetFlowBreak(monitors,true);
        fields.Controls.Add(new Label { Text="Position", AutoSize=true, Padding=new Padding(0,5,0,0) });
        sides.Items.AddRange(new object[] { "Right","Left","Above","Below" }); sides.SelectedIndex=(int)layout.Side; fields.Controls.Add(sides);
        fields.Controls.Add(new Label { Text="Alignment %", AutoSize=true, Padding=new Padding(12,5,0,0) });
        offset.Value=layout.OffsetPercent; fields.Controls.Add(offset);
        var center=new Button { Text="Center", AutoSize=true }; fields.Controls.Add(center);
        outer.Controls.Add(fields,0,1);
        canvas.RemoteSize=remoteSize; canvas.Value=layout with { Monitor=screens[monitors.SelectedIndex].DeviceName };
        outer.Controls.Add(canvas,0,2);
        outer.Controls.Add(new Label { Text="Edges snap together. Crossing works only where the screens touch.\n"+
            (connected ? "The Legion Go tile uses its connected desktop resolution." : "Connect first for the Legion Go's actual desktop size; this is a preview.")+"\nThis arranges switching between devices. Arrange each PC's own displays in Windows settings.",
            AutoSize=true, MaximumSize=new Size(700,0), Margin=new Padding(0,10,0,10) },0,3);
        var buttons=new FlowLayoutPanel { AutoSize=true, Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft };
        var apply=new Button { Text="Save layout", AutoSize=true, DialogResult=DialogResult.OK };
        var cancel=new Button { Text="Cancel", AutoSize=true, DialogResult=DialogResult.Cancel };
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel); outer.Controls.Add(buttons,0,4);
        AcceptButton=apply; CancelButton=cancel;
        monitors.SelectedIndexChanged+=(_,_) => RefreshValue(); sides.SelectedIndexChanged+=(_,_) => RefreshValue(); offset.ValueChanged+=(_,_) => RefreshValue();
        center.Click+=(_,_) => offset.Value=0;
        canvas.Edited+=value => {
            updating=true; sides.SelectedIndex=(int)value.Side; offset.Value=value.OffsetPercent; updating=false;
            LayoutValue=value;
        };
        apply.Click+=(_,_) => LayoutValue=canvas.Value;
        RefreshValue();
    }
    void RefreshValue()
    {
        if(updating) return;
        LayoutValue=new ScreenLayout { Monitor=screens[monitors.SelectedIndex].DeviceName, Side=(ScreenSide)sides.SelectedIndex, OffsetPercent=(int)offset.Value };
        canvas.Value=LayoutValue; canvas.Invalidate();
    }
}

internal sealed class LayoutCanvas : Control
{
    public ScreenLayout Value { get; set; }=new();
    public Size RemoteSize { get; set; }=new(1920,1080);
    public event Action<ScreenLayout>? Edited;
    float scale=1,originX,originY,dragScale,dragOriginX,dragOriginY;
    RectangleF remoteTile;
    bool dragging;
    PointF grab;
    public LayoutCanvas() { DoubleBuffered=true; BackColor=Color.FromArgb(29,36,48); Cursor=Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var local=Value.LocalBounds; var remote=Value.RemoteBounds(RemoteSize);
        var all=Rectangle.Union(local,remote);
        if(!dragging) {
            scale=Math.Max(0.001f,Math.Min((ClientSize.Width-60f)/Math.Max(1,all.Width),(ClientSize.Height-60f)/Math.Max(1,all.Height)));
            originX=(ClientSize.Width-all.Width*scale)/2-all.Left*scale;
            originY=(ClientSize.Height-all.Height*scale)/2-all.Top*scale;
        }
        RectangleF Map(Rectangle r) => new(originX+r.X*scale,originY+r.Y*scale,r.Width*scale,r.Height*scale);
        var localTile=Map(local); remoteTile=Map(remote);
        DrawTile(e.Graphics,localTile,"This PC",local.Size,Color.FromArgb(62,82,111));
        DrawTile(e.Graphics,remoteTile,"Legion Go",RemoteSize,Color.FromArgb(39,117,107));
        using var seam=new Pen(Color.FromArgb(128,245,202),4);
        if(Value.Side is ScreenSide.Right or ScreenSide.Left) {
            float x=(Value.Side==ScreenSide.Right ? local.Right : local.Left)*scale+originX;
            e.Graphics.DrawLine(seam,x,Math.Max(local.Top,remote.Top)*scale+originY,x,Math.Min(local.Bottom,remote.Bottom)*scale+originY);
        } else {
            float y=(Value.Side==ScreenSide.Below ? local.Bottom : local.Top)*scale+originY;
            e.Graphics.DrawLine(seam,Math.Max(local.Left,remote.Left)*scale+originX,y,Math.Min(local.Right,remote.Right)*scale+originX,y);
        }
    }
    void DrawTile(Graphics g,RectangleF tile,string name,Size pixels,Color color)
    {
        using var brush=new SolidBrush(color); using var border=new Pen(Color.FromArgb(170,205,222),1);
        g.FillRectangle(brush,tile); g.DrawRectangle(border,tile.X,tile.Y,tile.Width,tile.Height);
        TextRenderer.DrawText(g,$"{name}\n{pixels.Width} × {pixels.Height}",Font,Rectangle.Round(tile),Color.White,
            TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.Button!=MouseButtons.Left || !remoteTile.Contains(e.Location)) return;
        dragging=true; Capture=true;
        dragScale=scale; dragOriginX=originX; dragOriginY=originY;
        grab=new PointF((e.X-remoteTile.X)/scale,(e.Y-remoteTile.Y)/scale);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if(!dragging) return;
        var local=Value.LocalBounds;
        double x=(e.X-dragOriginX)/dragScale-grab.X+RemoteSize.Width/2.0;
        double y=(e.Y-dragOriginY)/dragScale-grab.Y+RemoteSize.Height/2.0;
        double dx=x-(local.Left+local.Width/2.0),dy=y-(local.Top+local.Height/2.0);
        bool horizontal=Math.Abs(dx)/Math.Max(1,local.Width+RemoteSize.Width)>Math.Abs(dy)/Math.Max(1,local.Height+RemoteSize.Height);
        var side=horizontal ? (dx<0 ? ScreenSide.Left : ScreenSide.Right) : (dy<0 ? ScreenSide.Above : ScreenSide.Below);
        double offset=horizontal ? dy*200/(local.Height+RemoteSize.Height) : dx*200/(local.Width+RemoteSize.Width);
        Value=Value with { Side=side,OffsetPercent=(int)Math.Round(Math.Clamp(offset,-95,95)) };
        Edited?.Invoke(Value); Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if(e.Button==MouseButtons.Left) { dragging=false; Capture=false; Invalidate(); } }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if(!Capture) { dragging=false; Invalidate(); } }
}
