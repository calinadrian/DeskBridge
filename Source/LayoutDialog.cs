using System.Drawing.Drawing2D;

namespace DeskBridge;

internal sealed class LayoutDialog : Form
{
    readonly ComboBox local=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=220 };
    readonly ComboBox remote=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=220 };
    readonly ComboBox side=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=110 };
    readonly NumericUpDown offset=new() { Minimum=-95, Maximum=95, Width=70 };
    readonly LayoutCanvas canvas=new() { Dock=DockStyle.Fill };
    readonly Screen[] screens=Screen.AllScreens;
    bool updating;
    public ScreenLayout LayoutValue { get; private set; }
    public LayoutDialog(ScreenLayout layout,Size remoteSize,bool connected)
    {
        LayoutValue=layout;
        Text="Arrange monitors"; ClientSize=new Size(860,650); MinimumSize=new Size(780,580);
        StartPosition=FormStartPosition.CenterParent;
        BackColor=Color.FromArgb(21,26,35); ForeColor=Color.White; Font=new Font("Segoe UI",10);
        var outer=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=4 };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(outer);
        var fields=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill };
        var detected=layout.RemoteScreens.Length>0 ? layout.RemoteScreens : new[] { new Rectangle(Point.Empty,remoteSize) };
        for(int i=0;i<detected.Length;i++) remote.Items.Add($"Remote {i+1}: {detected[i].Width} × {detected[i].Height}");
        foreach(var item in screens.Select((s,i)=>(s,i))) local.Items.Add($"This PC {item.i+1}: {item.s.Bounds.Width} × {item.s.Bounds.Height}");
        side.Items.AddRange(new object[] { "Right","Left","Above","Below" });
        fields.Controls.Add(new Label { Text="Move monitor",AutoSize=true,Padding=new Padding(0,5,0,0) }); fields.Controls.Add(remote);
        fields.Controls.Add(new Label { Text="Next to",AutoSize=true,Padding=new Padding(0,5,0,0) }); fields.Controls.Add(local); fields.SetFlowBreak(local,true);
        fields.Controls.Add(new Label { Text="Position",AutoSize=true,Padding=new Padding(0,5,0,0) }); fields.Controls.Add(side);
        fields.Controls.Add(new Label { Text="Alignment %",AutoSize=true,Padding=new Padding(10,5,0,0) }); fields.Controls.Add(offset);
        outer.Controls.Add(fields,0,0);
        var placements=Enumerable.Range(0,detected.Length).Select(i=>layout.Placements.FirstOrDefault(p=>p.Index==i)
            ?? new MonitorPlacement(i,layout.Monitor,i==layout.RemoteMonitor ? layout.Side : ScreenSide.Right,i==layout.RemoteMonitor ? layout.OffsetPercent : 0)).ToArray();
        canvas.Value=layout with { RemoteScreens=detected,Placements=placements }; canvas.RemoteSize=remoteSize;
        outer.Controls.Add(canvas,0,1);
        outer.Controls.Add(new Label { Text="Drag each remote monitor independently to an edge of a PC monitor. Green seams allow switching.\nFor your desk: put the Legion Go below this PC and its external monitor on the right.\n"+
            (connected ? "Remote monitor numbers follow DeskBridge's detected order. " : "Connect first to detect both remote displays. ")+"Windows display settings still control movement between screens on the same device.",AutoSize=true,MaximumSize=new Size(780,0),Margin=new Padding(0,12,0,12) },0,2);
        var buttons=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft };
        var save=new Button { Text="Save layout",AutoSize=true }; var cancel=new Button { Text="Cancel",AutoSize=true,DialogResult=DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); outer.Controls.Add(buttons,0,3); CancelButton=cancel; AcceptButton=save;
        void ReadSelection() {
            updating=true;
            var p=canvas.Value.Placements[canvas.Selected];
            remote.SelectedIndex=canvas.Selected;
            local.SelectedIndex=Math.Max(0,Array.FindIndex(screens,s=>s.DeviceName==p.LocalMonitor));
            side.SelectedIndex=(int)p.Side; offset.Value=Math.Clamp(p.OffsetPercent,-95,95); updating=false;
        }
        void WriteSelection() {
            if(updating || local.SelectedIndex<0 || side.SelectedIndex<0) return;
            var values=canvas.Value.Placements.ToArray();
            values[canvas.Selected]=new MonitorPlacement(canvas.Selected,screens[local.SelectedIndex].DeviceName,(ScreenSide)side.SelectedIndex,(int)offset.Value);
            canvas.Value=canvas.Value with { Placements=values }; canvas.Invalidate();
        }
        remote.SelectedIndexChanged+=(_,_)=>{ if(!updating) { canvas.Selected=remote.SelectedIndex; ReadSelection(); canvas.Invalidate(); } };
        local.SelectedIndexChanged+=(_,_)=>WriteSelection(); side.SelectedIndexChanged+=(_,_)=>WriteSelection(); offset.ValueChanged+=(_,_)=>WriteSelection();
        canvas.Edited+=ReadSelection;
        save.Click+=(_,_)=>{
            var routes=canvas.Value.Routes(remoteSize).ToArray();
            var boxes=routes.Select(r=>r.PortalBounds(remoteSize)).ToArray();
            for(int i=0;i<boxes.Length;i++) {
                if(screens.Any(s=>s.Bounds.IntersectsWith(boxes[i])) || boxes.Take(i).Any(b=>b.IntersectsWith(boxes[i]))) {
                    MessageBox.Show(this,"Some monitors overlap. Move each remote tile to a separate touching edge, or adjust its alignment.","Arrange monitors"); return;
                }
                if(routes[i].PortalSpans(remoteSize).Count==0) {
                    MessageBox.Show(this,"Each remote monitor needs a green switching seam with this PC. Choose another edge or alignment.","Arrange monitors"); return;
                }
            }
            LayoutValue=canvas.Value with { RemoteMonitor=canvas.Selected }; DialogResult=DialogResult.OK;
        };
        ReadSelection();
    }
}

internal sealed class LayoutCanvas : Control
{
    public ScreenLayout Value { get; set; }=new();
    public Size RemoteSize { get; set; }=new(1920,1080);
    public int Selected { get; set; }
    public event Action? Edited;
    float scale=1,originX,originY;
    readonly List<RectangleF> tiles=new();
    bool dragging;
    PointF grab;
    public LayoutCanvas() { DoubleBuffered=true;BackColor=Color.FromArgb(29,36,48);Cursor=Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var routes=Value.Routes(RemoteSize).ToArray();
        var all=Screen.AllScreens.Select(s=>s.Bounds).Concat(routes.Select(r=>r.PortalBounds(RemoteSize))).Aggregate(Rectangle.Union);
        if(!dragging) {
            scale=Math.Max(.001f,Math.Min((Width-60f)/all.Width,(Height-60f)/all.Height));
            originX=(Width-all.Width*scale)/2-all.Left*scale; originY=(Height-all.Height*scale)/2-all.Top*scale;
        }
        RectangleF Map(Rectangle r)=>new(originX+r.X*scale,originY+r.Y*scale,r.Width*scale,r.Height*scale);
        foreach(var item in Screen.AllScreens.Select((s,i)=>(s,i))) DrawTile(e.Graphics,Map(item.s.Bounds),$"This PC {item.i+1}",Color.FromArgb(62,82,111));
        tiles.Clear();
        for(int i=0;i<routes.Length;i++) {
            var tile=Map(routes[i].PortalBounds(RemoteSize)); tiles.Add(tile);
            DrawTile(e.Graphics,tile,$"Remote {routes[i].RemoteMonitor+1}",i==Selected ? Color.FromArgb(39,117,107) : Color.FromArgb(31,68,65));
            var local=routes[i].LocalBounds;
            using var seam=new Pen(Color.FromArgb(128,245,202),4);
            foreach(var span in routes[i].PortalSpans(RemoteSize)) {
                if(routes[i].Side is ScreenSide.Right or ScreenSide.Left) {
                    float x=originX+(routes[i].Side==ScreenSide.Right ? local.Right : local.Left)*scale;
                    e.Graphics.DrawLine(seam,x,originY+span.Start*scale,x,originY+span.End*scale);
                } else {
                    float y=originY+(routes[i].Side==ScreenSide.Below ? local.Bottom : local.Top)*scale;
                    e.Graphics.DrawLine(seam,originX+span.Start*scale,y,originX+span.End*scale,y);
                }
            }
        }
    }
    void DrawTile(Graphics g,RectangleF r,string text,Color color)
    {
        using var brush=new SolidBrush(color); using var pen=new Pen(Color.LightSteelBlue);
        g.FillRectangle(brush,r); g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height);
        TextRenderer.DrawText(g,text,Font,Rectangle.Round(r),Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if(e.Button!=MouseButtons.Left) return;
        int index=tiles.FindLastIndex(t=>t.Contains(e.Location)); if(index<0) return;
        Selected=index; grab=new PointF((e.X-tiles[index].X)/scale,(e.Y-tiles[index].Y)/scale);
        dragging=true; Capture=true; Edited?.Invoke(); Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if(!dragging) return;
        var size=Value.RemoteScreens[Selected].Size;
        double x=(e.X-originX)/scale-grab.X+size.Width/2.0,y=(e.Y-originY)/scale-grab.Y+size.Height/2.0;
        var screen=Screen.AllScreens.MinBy(s=>Math.Pow(x-(s.Bounds.Left+s.Bounds.Width/2.0),2)+Math.Pow(y-(s.Bounds.Top+s.Bounds.Height/2.0),2))!;
        var b=screen.Bounds; double dx=x-(b.Left+b.Width/2.0),dy=y-(b.Top+b.Height/2.0);
        bool horizontal=Math.Abs(dx)/(b.Width+size.Width)>Math.Abs(dy)/(b.Height+size.Height);
        var side=horizontal ? (dx<0 ? ScreenSide.Left : ScreenSide.Right) : (dy<0 ? ScreenSide.Above : ScreenSide.Below);
        double offset=horizontal ? dy*200/(b.Height+size.Height) : dx*200/(b.Width+size.Width);
        var placements=Value.Placements.ToArray();
        placements[Selected]=new MonitorPlacement(Selected,screen.DeviceName,side,(int)Math.Round(Math.Clamp(offset,-95,95)));
        Value=Value with { Placements=placements }; Edited?.Invoke(); Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if(e.Button==MouseButtons.Left) { dragging=false; Capture=false; Invalidate(); } }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if(!Capture) { dragging=false; Invalidate(); } }
}
