using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskBridge;

internal sealed class MainForm : Form
{
    readonly RadioButton share=new() { Text="Share this keyboard and mouse", Checked=true, AutoSize=true };
    readonly RadioButton receive=new() { Text="Receive control on this device", AutoSize=true };
    readonly TextBox address=new() { PlaceholderText="Legion Go address, e.g. 192.168.1.42", Dock=DockStyle.Fill };
    readonly TextBox code=new() { Multiline=true, Height=76, Dock=DockStyle.Fill, ScrollBars=ScrollBars.Vertical };
    readonly ComboBox side=new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=150 };
    readonly CheckBox edges=new() { Text="Switch when the pointer reaches the screen edge", Checked=true, AutoSize=true };
    readonly Button action=new() { Text="Connect", AutoSize=true, MinimumSize=new Size(170,42) };
    readonly Button stop=new() { Text="Disconnect", AutoSize=true, Enabled=false, MinimumSize=new Size(130,42) };
    readonly Button copy=new() { Text="Copy pairing code", AutoSize=true, Enabled=false };
    readonly Button local=new() { Text="Return control here", AutoSize=true, Enabled=false };
    readonly Label status=new() { Text="Ready to connect", AutoSize=true, ForeColor=Color.FromArgb(104,216,192), MaximumSize=new Size(590,0) };
    readonly Label help=new() { AutoSize=true, MaximumSize=new Size(590,0) };
    readonly Label ips=new() { AutoSize=true, MaximumSize=new Size(590,0), ForeColor=Color.FromArgb(104,216,192) };
    readonly Panel senderPanel=new() { AutoSize=true, Dock=DockStyle.Top };
    readonly NotifyIcon tray=new() { Icon=SystemIcons.Application, Text="DeskBridge — local control", Visible=true };
    readonly System.Windows.Forms.Timer dimensions=new() { Interval=2000 };
    readonly System.Windows.Forms.Timer updateTimer=new() { Interval=6*60*60*1000 };
    readonly Button updateButton=new() { Text="Check for updates", AutoSize=true };
    readonly Label updateStatus=new() { AutoSize=true, MaximumSize=new Size(590,0), ForeColor=Color.FromArgb(169,181,200) };
    readonly CancellationTokenSource lifetime=new();
    ReleaseUpdate? availableUpdate;
    bool updateBusy;
    CancellationTokenSource? session;
    TcpListener? listener;
    Connection? connection;
    InputCapture? capture;
    InputSink? sink;
    bool receiving;

    public MainForm()
    {
        Text="DeskBridge "+Updater.DisplayVersion; ClientSize=new Size(700,820); MinimumSize=new Size(640,680);
        StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",11); BackColor=Color.FromArgb(21,26,35); ForeColor=Color.FromArgb(230,236,244);
        var root=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown,
            WrapContents=false, AutoScroll=true, Padding=new Padding(30,22,24,22) };
        Controls.Add(root);
        root.Controls.Add(new Label { Text="One desk. One set of controls.", Font=new Font("Segoe UI",22,FontStyle.Bold), AutoSize=true, Margin=new Padding(0,0,0,8) });
        root.Controls.Add(new Label { Text="DESKBRIDGE  /  WINDOWS PREVIEW", AutoSize=true, ForeColor=Color.FromArgb(104,216,192), Margin=new Padding(0,0,0,22) });
        updateStatus.Text="Version "+Updater.DisplayVersion+" · Updates from GitHub Releases";
        root.Controls.Add(updateStatus);
        updateButton.Margin=new Padding(0,5,0,16); root.Controls.Add(updateButton);
        var roles=new FlowLayoutPanel { AutoSize=true, FlowDirection=FlowDirection.TopDown, WrapContents=false, Margin=new Padding(0,0,0,16) };
        roles.Controls.Add(share); roles.Controls.Add(receive); root.Controls.Add(roles);
        root.Controls.Add(help);
        root.Controls.Add(ips);
        var senderFields=new FlowLayoutPanel { AutoSize=true, Dock=DockStyle.Top, FlowDirection=FlowDirection.TopDown, WrapContents=false };
        senderFields.Controls.Add(new Label { Text="Legion Go address", AutoSize=true, Margin=new Padding(0,12,0,5) });
        address.Width=590; address.Dock=DockStyle.None; senderFields.Controls.Add(address);
        side.Items.AddRange(new object[] { "On my right", "On my left" }); side.SelectedIndex=0;
        var placement=new FlowLayoutPanel { AutoSize=true, Margin=new Padding(0,10,0,4) };
        placement.Controls.Add(new Label { Text="The Legion Go is", AutoSize=true, Padding=new Padding(0,5,8,0) }); placement.Controls.Add(side);
        senderFields.Controls.Add(placement); senderFields.Controls.Add(edges);
        senderPanel.Width=600; senderPanel.Controls.Add(senderFields); root.Controls.Add(senderPanel);
        root.Controls.Add(new Label { Text="Private pairing code", AutoSize=true, Margin=new Padding(0,14,0,5) });
        code.Width=590; code.Dock=DockStyle.None; root.Controls.Add(code);
        copy.Margin=new Padding(0,6,0,12); root.Controls.Add(copy);
        var buttons=new FlowLayoutPanel { AutoSize=true, Margin=new Padding(0,4,0,12) };
        buttons.Controls.Add(action); buttons.Controls.Add(stop); root.Controls.Add(buttons);
        root.Controls.Add(status);
        local.Margin=new Padding(0,10,0,12); root.Controls.Add(local);
        root.Controls.Add(new Label { Text="Ctrl + Alt + Space  ·  Switch devices\nCtrl + Alt + Esc  ·  Return here / stop receiving\n\nKeep both devices on the same home network.\nMinimize to keep DeskBridge running in the tray; closing exits.", AutoSize=true, ForeColor=Color.FromArgb(169,181,200), Margin=new Padding(0,12,0,0) });
        Style(root);
        share.CheckedChanged+=(_,_) => Mode();
        side.SelectedIndexChanged+=(_,_) => { if(capture!=null) capture.RightSide=side.SelectedIndex==0; };
        edges.CheckedChanged+=(_,_) => { if(capture!=null) capture.EdgeEnabled=edges.Checked; };
        action.Click+=async (_,_) => await BeginSession();
        stop.Click+=(_,_) => StopSession("Disconnected. Control is local.");
        local.Click+=(_,_) => capture?.ReturnLocal();
        copy.Click+=(_,_) => { try { Clipboard.SetText(code.Text); SetStatus("Pairing code copied."); } catch { SetStatus("Clipboard is busy. Select the code and copy it manually."); } };
        dimensions.Tick+=(_,_) => SendDimensions();
        updateButton.Click+=async (_,_) => { if(availableUpdate!=null) await InstallUpdate(); else await CheckUpdate(); };
        updateTimer.Tick+=async (_,_) => await CheckUpdate();
        var menu=new ContextMenuStrip();
        menu.Items.Add("Open DeskBridge",null,(_,_) => Open());
        menu.Items.Add("Return control here",null,(_,_) => capture?.ReturnLocal());
        menu.Items.Add("Disconnect",null,(_,_) => StopSession("Disconnected. Control is local."));
        menu.Items.Add("Updates",null,async (_,_) => { Open(); await CheckUpdate(); });
        menu.Items.Add("Exit",null,(_,_) => Close());
        tray.ContextMenuStrip=menu; tray.DoubleClick+=(_,_) => Open();
        Resize+=(_,_) => { if(WindowState==FormWindowState.Minimized) Hide(); };
        FormClosing+=(_,_) => { lifetime.Cancel(); updateTimer.Dispose(); StopSession(""); dimensions.Dispose(); tray.Visible=false; tray.Dispose(); };
        Shown+=async (_,_) => { updateTimer.Start(); await CheckUpdate(); };
        Mode();
    }
    static void Style(Control control)
    {
        foreach(Control c in control.Controls) {
            if(c is Button b) {
                b.FlatStyle=FlatStyle.Flat; b.BackColor=Color.FromArgb(43,57,75); b.ForeColor=Color.White;
                b.FlatAppearance.BorderColor=Color.FromArgb(78,103,129); b.Padding=new Padding(9,4,9,4);
            } else if(c is TextBox or ComboBox) { c.BackColor=Color.FromArgb(33,41,54); c.ForeColor=Color.White; }
            Style(c);
        }
    }
    void Mode()
    {
        senderPanel.Visible=share.Checked; copy.Visible=receive.Checked; code.ReadOnly=receive.Checked;
        code.Clear(); ips.Text="";
        action.Text=share.Checked ? "Connect" : "Create pairing code";
        help.Text=share.Checked
            ? "On the Legion Go, choose Receive control and create a pairing code.\nEnter its address and paste its code here."
            : "Create a pairing code here, then enter it on the PC with your keyboard\nand mouse. Only a device with this code can connect.";
    }
    void Busy(bool busy)
    {
        share.Enabled=receive.Enabled=!busy; address.Enabled=!busy; action.Enabled=!busy;
        code.ReadOnly=busy || receive.Checked; stop.Enabled=busy; copy.Enabled=busy && receiving;
        local.Enabled=busy && !receiving;
    }
    void SetStatus(string text) { status.Text=text; }
    void UI(Action action)
    {
        if(IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if(!IsDisposed && !Disposing) action(); })); } catch(InvalidOperationException) { }
    }
    void Open() { Show(); WindowState=FormWindowState.Normal; Activate(); }
    async Task CheckUpdate()
    {
        if(updateBusy || lifetime.IsCancellationRequested) return;
        updateBusy=true; updateButton.Enabled=false;
        updateStatus.Text="Checking GitHub for updates…";
        try {
            availableUpdate=await Updater.Check(lifetime.Token);
            if(lifetime.IsCancellationRequested) return;
            updateStatus.Text=availableUpdate==null ? "Version "+Updater.DisplayVersion+" · You're up to date."
                : $"Version {availableUpdate.Version} is available. Updating disconnects and restarts this app.";
        } catch(OperationCanceledException) {
            if(!lifetime.IsCancellationRequested) updateStatus.Text="Update check timed out. Try again when you're online.";
        } catch(Exception ex) { if(!lifetime.IsCancellationRequested) updateStatus.Text="Could not check for updates: "+ex.Message; }
        finally {
            updateBusy=false;
            if(!lifetime.IsCancellationRequested) { updateButton.Enabled=true; updateButton.Text=availableUpdate==null ? "Check for updates" : "Update & restart"; }
        }
    }
    async Task InstallUpdate()
    {
        if(updateBusy || availableUpdate==null || lifetime.IsCancellationRequested) return;
        updateBusy=true; updateButton.Enabled=false;
        updateStatus.Text="Downloading update…";
        try {
            var progress=new Progress<int>(percent => {
                if(!lifetime.IsCancellationRequested) updateStatus.Text=percent<0 ? "Downloading update…" : $"Downloading update… {percent}%";
            });
            var job=await Updater.Download(availableUpdate,progress,lifetime.Token);
            if(lifetime.IsCancellationRequested) return;
            StopSession("Installing update…");
            Updater.Launch(job);
            Close();
        } catch(OperationCanceledException) {
            if(!lifetime.IsCancellationRequested) updateStatus.Text="Download timed out. Click Update & restart to try again.";
        } catch(Exception ex) { if(!lifetime.IsCancellationRequested) updateStatus.Text="Update could not be installed: "+ex.Message; }
        finally { updateBusy=false; if(!lifetime.IsCancellationRequested) updateButton.Enabled=true; }
    }
    async Task BeginSession()
    {
        if(session!=null) return;
        receiving=receive.Checked;
        if(!receiving && (string.IsNullOrWhiteSpace(address.Text)||string.IsNullOrWhiteSpace(code.Text))) {
            SetStatus("Enter the Legion Go address and pairing code first."); return;
        }
        var current=session=new CancellationTokenSource();
        Busy(true);
        try {
            if(receiving) {
                if(!Native.RegisterHotKey(Handle,1,0x4003,0x1B))
                    throw new InvalidOperationException("Ctrl + Alt + Esc is already in use. Close the other app using it, then try again.");
                using var cert=Connection.Certificate();
                var secret=RandomNumberGenerator.GetBytes(32);
                listener=new TcpListener(IPAddress.Any,Connection.Port); listener.Start(1);
                code.Text=Connection.PairCode(cert,secret);
                ips.Text="This device: "+string.Join("  /  ",LocalAddresses());
                SetStatus("Waiting for your PC. Share this address and pairing code with it.");
                while(!current.IsCancellationRequested) {
                    var tcp=await listener.AcceptTcpClientAsync(current.Token);
                    Connection? paired=null;
                    try { paired=await Connection.Accept(tcp,cert,secret,current.Token); }
                    catch(Exception) when(!current.IsCancellationRequested) {
                        SetStatus("A connection was rejected. Still waiting for the correct pairing code."); continue;
                    }
                    if(session!=current || current.IsCancellationRequested) { paired?.Dispose(); return; }
                    Attach(paired!); listener.Stop(); listener=null;
                    code.Clear(); copy.Enabled=false;
                    SetStatus("Connected. This device is ready to receive your keyboard and mouse.");
                    break;
                }
            } else {
                SetStatus("Connecting securely…");
                var paired=await Connection.Connect(address.Text,code.Text,current.Token);
                if(session!=current || current.IsCancellationRequested) { paired.Dispose(); return; }
                try { capture=new InputCapture { RightSide=side.SelectedIndex==0, EdgeEnabled=edges.Checked }; }
                catch { paired.Dispose(); throw; }
                capture.Changed+=active => {
                    SetStatus(active ? "Controlling the Legion Go — Ctrl + Alt + Esc brings you back." : "Connected — controlling this PC. Move to the edge or press Ctrl + Alt + Space.");
                    tray.Text=active ? "DeskBridge — controlling Legion Go" : "DeskBridge — local control";
                };
                Attach(paired); capture.Connection=paired;
                SetStatus("Connected — waiting for the Legion Go screen size…");
                code.Clear();
            }
        } catch(Exception ex) {
            if(session==current) StopSession(current.IsCancellationRequested ? "Disconnected." : "Could not connect: "+ex.Message);
        }
    }
    void Attach(Connection paired)
    {
        connection=paired;
        var target=receiving ? sink=new InputSink() : null;
        bool isReceiver=receiving;
        paired.Received+=p => {
            if(isReceiver) {
                try {
                    target!.Handle(p);
                    if(p.Kind is Kind.Start or Kind.Release) UI(() => {
                        if(connection!=paired) return;
                        bool active=p.Kind==Kind.Start;
                        SetStatus(active ? "Your PC is controlling this device. Ctrl + Alt + Esc here stops receiving." : "Connected. Waiting for your PC to switch control here.");
                        tray.Text=active ? "DeskBridge — receiving control" : "DeskBridge — connected, idle";
                    });
                }
                catch { UI(() => { if(connection==paired) StopSession("Windows blocked remote input. Check the target app's permissions."); }); paired.Dispose(); }
            } else if(p.Kind==Kind.Hello) UI(() => {
                if(connection!=paired) return;
                capture?.SetRemoteSize(p.A,p.B);
                if(capture?.Active!=true) SetStatus("Connected — move to the edge or press Ctrl + Alt + Space.");
            });
        };
        paired.Closed+=() => {
            target?.Close();
            UI(() => { if(connection==paired) StopSession("Connection ended. Control is local. Connect again to resume."); });
        };
        paired.Start();
        if(receiving) { SendDimensions(); dimensions.Start(); }
    }
    void SendDimensions()
    {
        if(!receiving || connection?.Alive!=true) return;
        var b=SystemInformation.VirtualScreen; connection.Send(new Packet(Kind.Hello,b.Width,b.Height));
    }
    void StopSession(string text)
    {
        var previous=session; session=null;
        previous?.Cancel(); listener?.Stop(); listener=null;
        dimensions.Stop(); Native.UnregisterHotKey(Handle,1);
        var old=connection; connection=null;
        capture?.Dispose(); capture=null;
        old?.Dispose(); sink?.Close(); sink=null;
        if(receiving) code.Clear();
        Busy(false); copy.Enabled=false; ips.Text="";
        tray.Text="DeskBridge — local control"; SetStatus(text);
    }
    static IEnumerable<string> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString()).Distinct();
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x312 && m.WParam.ToInt32()==1) { StopSession("Receiving stopped. Create a new pairing code to reconnect."); Open(); }
        base.WndProc(ref m);
    }
}
