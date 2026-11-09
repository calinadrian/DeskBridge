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
    readonly Button arrange=new() { Text="Arrange monitors…", AutoSize=true };
    readonly Label layoutSummary=new() { AutoSize=true, MaximumSize=new Size(580,0) };
    readonly CheckBox edges=new() { Text="Switch when the pointer reaches the screen edge", Checked=true, AutoSize=true };
    readonly CheckBox fullscreen=new() { Text="Pause edge switching in fullscreen apps and games", Checked=true, AutoSize=true };
    readonly CheckBox lockLocal=new() { Text="Lock control to this PC (for games)", AutoSize=true };
    readonly CheckBox relative=new() { Text="Relative mouse for remote games — switch with shortcut only", AutoSize=true };
    readonly CheckBox remember=new() { Text="Remember this connection on this Windows account",AutoSize=true };
    readonly CheckBox automatic=new() { Text="Connect automatically when DeskBridge opens (retry if offline)",AutoSize=true };
    readonly CheckBox startup=new() { Text="Start DeskBridge with Windows",AutoSize=true };
    readonly Button forget=new() { Text="Forget saved pairing",AutoSize=true };
    readonly System.Windows.Forms.Timer reconnect=new() { Interval=15000 };
    bool reconnectWanted,changingStartup;
    Preferences preferences=Preferences.Load();
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
    bool peerSupportsDisplays;
    int expectedDisplays;
    readonly List<Rectangle> incomingDisplays=new();

    public MainForm(bool startupLaunch=false)
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
        var placement=new FlowLayoutPanel { AutoSize=true, Margin=new Padding(0,10,0,4) };
        placement.Controls.Add(arrange);
        senderFields.Controls.Add(placement); senderFields.Controls.Add(layoutSummary);
        edges.Checked=preferences.EdgeEnabled; fullscreen.Checked=preferences.PauseFullscreen;
        relative.Checked=preferences.RelativeMouse; lockLocal.Checked=preferences.LockLocal;
        layoutSummary.Text=preferences.Layout.ToString();
        senderFields.Controls.Add(edges); senderFields.Controls.Add(fullscreen); senderFields.Controls.Add(lockLocal); senderFields.Controls.Add(relative);
        senderPanel.Width=600; senderPanel.Controls.Add(senderFields); root.Controls.Add(senderPanel);
        root.Controls.Add(new Label { Text="Private pairing code", AutoSize=true, Margin=new Padding(0,14,0,5) });
        code.Width=590; code.Dock=DockStyle.None; root.Controls.Add(code);
        copy.Margin=new Padding(0,6,0,12); root.Controls.Add(copy);
        var buttons=new FlowLayoutPanel { AutoSize=true, Margin=new Padding(0,4,0,12) };
        buttons.Controls.Add(action); buttons.Controls.Add(stop); root.Controls.Add(buttons);
        remember.Checked=preferences.RememberConnection; automatic.Checked=preferences.AutoConnect; startup.Checked=preferences.StartWithWindows;
        automatic.Enabled=remember.Checked;
        root.Controls.Add(remember); root.Controls.Add(automatic); root.Controls.Add(startup); root.Controls.Add(forget);
        root.Controls.Add(status);
        local.Margin=new Padding(0,10,0,12); root.Controls.Add(local);
        root.Controls.Add(new Label { Text="Ctrl + Alt + Space  ·  Switch devices\nCtrl + Alt + Esc  ·  Return here / stop receiving\n\nKeep both devices on the same home network.\nMinimize to keep DeskBridge running in the tray; closing exits.", AutoSize=true, ForeColor=Color.FromArgb(169,181,200), Margin=new Padding(0,12,0,0) });
        Style(root);
        address.Text=preferences.Address;
        receive.Checked=preferences.ReceiveMode; share.Checked=!preferences.ReceiveMode;
        share.CheckedChanged+=(_,_) => { if(share.Checked || receive.Checked) { Mode(); PersistConnectionOptions(); } };
        address.TextChanged+=(_,_) => PersistConnectionOptions();
        remember.CheckedChanged+=(_,_) => {
            automatic.Enabled=remember.Checked;
            if(!remember.Checked) {
                automatic.Checked=false; reconnectWanted=false;
                try { SavedConnection.Forget(); } catch(Exception ex) { SetStatus("Could not forget pairing: "+ex.Message); }
            }
            PersistConnectionOptions();
        };
        automatic.CheckedChanged+=(_,_) => { reconnectWanted=automatic.Checked; stop.Enabled=session!=null || reconnectWanted; PersistConnectionOptions(); };
        startup.CheckedChanged+=(_,_) => {
            if(changingStartup) return;
            try { SavedConnection.SetStartup(startup.Checked); PersistConnectionOptions(); }
            catch(Exception ex) { changingStartup=true; startup.Checked=preferences.StartWithWindows; changingStartup=false; SetStatus("Could not change Windows startup: "+ex.Message); }
        };
        forget.Click+=(_,_) => {
            reconnectWanted=false; automatic.Checked=false; StopSession("Saved pairing forgotten. Create a new code on the receiving device.");
            try { SavedConnection.Forget(); code.Clear(); } catch(Exception ex) { SetStatus("Could not forget pairing: "+ex.Message); }
        };
        reconnect.Tick+=async (_,_) => { if(reconnectWanted && session==null) await BeginSession(); };
        arrange.Click+=(_,_) => {
            capture?.ReturnLocal();
            using var dialog=new LayoutDialog(preferences.Layout with { RemoteScreens=capture?.RemoteScreens ?? preferences.Layout.RemoteScreens },capture?.RemoteSize ?? new Size(1920,1080),capture?.RemoteReady==true);
            if(dialog.ShowDialog(this)!=DialogResult.OK) return;
            preferences=preferences with { Layout=dialog.LayoutValue }; SavePreferences();
        };
        edges.CheckedChanged+=(_,_) => SavePreferences();
        fullscreen.CheckedChanged+=(_,_) => SavePreferences();
        lockLocal.CheckedChanged+=(_,_) => SavePreferences();
        relative.CheckedChanged+=(_,_) => SavePreferences();
        action.Click+=async (_,_) => { reconnectWanted=automatic.Checked; await BeginSession(); };
        stop.Click+=(_,_) => { reconnectWanted=false; StopSession("Disconnected. Control is local."); };
        local.Click+=(_,_) => capture?.ReturnLocal();
        copy.Click+=(_,_) => { try { Clipboard.SetText(code.Text); SetStatus("Pairing code copied."); } catch { SetStatus("Clipboard is busy. Select the code and copy it manually."); } };
        dimensions.Tick+=(_,_) => SendDimensions();
        updateButton.Click+=async (_,_) => { if(availableUpdate!=null) await InstallUpdate(); else await CheckUpdate(); };
        updateTimer.Tick+=async (_,_) => await CheckUpdate();
        var menu=new ContextMenuStrip();
        menu.Items.Add("Open DeskBridge",null,(_,_) => Open());
        menu.Items.Add("Return control here",null,(_,_) => capture?.ReturnLocal());
        menu.Items.Add("Disconnect",null,(_,_) => { reconnectWanted=false; StopSession("Disconnected. Control is local."); });
        menu.Items.Add("Updates",null,async (_,_) => { Open(); await CheckUpdate(); });
        menu.Items.Add("Exit",null,(_,_) => Close());
        tray.ContextMenuStrip=menu; tray.DoubleClick+=(_,_) => Open();
        Resize+=(_,_) => { if(WindowState==FormWindowState.Minimized) Hide(); };
        FormClosing+=(_,_) => { lifetime.Cancel(); reconnectWanted=false; reconnect.Dispose(); updateTimer.Dispose(); StopSession(""); dimensions.Dispose(); tray.Visible=false; tray.Dispose(); };
        Shown+=async (_,_) => {
            updateTimer.Start(); reconnect.Start();
            reconnectWanted=automatic.Checked && remember.Checked;
            if(startupLaunch) { WindowState=FormWindowState.Minimized; Hide(); }
            if(reconnectWanted) _=BeginSession();
            await CheckUpdate();
        };
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
        if(share.Checked && preferences.RememberConnection) {
            try { code.Text=SavedConnection.SenderCode(); } catch(Exception ex) { SetStatus("Could not read saved pairing: "+ex.Message); }
        }
        action.Text=share.Checked ? "Connect" : "Receive / show pairing code";
        help.Text=share.Checked
            ? "On the Legion Go, choose Receive control and create a pairing code.\nEnter its address and paste its code here."
            : "Create a pairing code here, then enter it on the PC with your keyboard\nand mouse. Only a device with this code can connect.";
    }
    void PersistConnectionOptions()
    {
        preferences=preferences with { ReceiveMode=receive.Checked,Address=address.Text.Trim(),RememberConnection=remember.Checked,
            AutoConnect=automatic.Checked && remember.Checked,StartWithWindows=startup.Checked };
        try { preferences.Save(); } catch(Exception ex) { SetStatus("Could not save connection options: "+ex.Message); }
    }
    void Busy(bool busy)
    {
        share.Enabled=receive.Enabled=remember.Enabled=!busy; address.Enabled=!busy; action.Enabled=!busy;
        code.ReadOnly=busy || receive.Checked; stop.Enabled=busy || reconnectWanted; copy.Enabled=busy && receiving;
        local.Enabled=busy && !receiving;
    }
    void SetStatus(string text) { status.Text=text; }
    void UI(Action action)
    {
        if(IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if(!IsDisposed && !Disposing) action(); })); } catch(InvalidOperationException) { }
    }
    void Open() { Show(); WindowState=FormWindowState.Normal; Activate(); }
    void SavePreferences()
    {
        capture?.ReturnLocal();
        preferences=preferences with { EdgeEnabled=edges.Checked, PauseFullscreen=fullscreen.Checked, LockLocal=lockLocal.Checked, RelativeMouse=relative.Checked };
        layoutSummary.Text=preferences.Layout.ToString();
        if(capture!=null) {
            capture.Layout=preferences.Layout with { RemoteScreens=capture.RemoteScreens }; capture.EdgeEnabled=preferences.EdgeEnabled;
            capture.PauseFullscreen=preferences.PauseFullscreen; capture.LockLocal=preferences.LockLocal; capture.RelativeMouse=preferences.RelativeMouse;
            ShowConnectedStatus();
        }
        try { preferences.Save(); } catch(Exception ex) { SetStatus("Could not save your layout and options: "+ex.Message); }
    }
    void ShowConnectedStatus()
    {
        if(capture?.Active==true || connection?.Alive!=true) return;
        if(lockLocal.Checked) SetStatus("Connected — control is locked to this PC. Uncheck the lock to switch devices.");
        else if(relative.Checked && capture?.RemoteProtocol<2) SetStatus("Relative game mode needs v0.1.2 or newer on the receiving device. Update both devices.");
        else if(relative.Checked) SetStatus("Connected — game mode. Ctrl + Alt + Space shares input; Ctrl + Alt + Esc returns here.");
        else SetStatus("Connected — move across the touching edge or press Ctrl + Alt + Space.");
    }
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
        PersistConnectionOptions();
        if(!receiving && string.IsNullOrWhiteSpace(code.Text) && remember.Checked) {
            try { code.Text=SavedConnection.SenderCode(); } catch(Exception ex) { SetStatus("Could not read saved pairing: "+ex.Message); return; }
        }
        if(!receiving && (string.IsNullOrWhiteSpace(address.Text)||string.IsNullOrWhiteSpace(code.Text))) {
            SetStatus("Enter the Legion Go address and pairing code first."); return;
        }
        var current=session=new CancellationTokenSource();
        Busy(true);
        try {
            if(receiving) {
                if(!Native.RegisterHotKey(Handle,1,0x4003,0x1B))
                    throw new InvalidOperationException("Ctrl + Alt + Esc is already in use. Close the other app using it, then try again.");
                var identity=SavedConnection.Receiver(remember.Checked);
                using var cert=identity.Certificate;
                var secret=identity.Secret;
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
                var pairingCode=code.Text.Trim();
                var paired=await Connection.Connect(address.Text,pairingCode,current.Token);
                if(session!=current || current.IsCancellationRequested) { paired.Dispose(); return; }
                try { capture=new InputCapture {
                    Layout=preferences.Layout with { RemoteScreens=Array.Empty<Rectangle>() }, EdgeEnabled=edges.Checked, PauseFullscreen=fullscreen.Checked,
                    RelativeMouse=relative.Checked, LockLocal=lockLocal.Checked }; }
                catch { paired.Dispose(); throw; }
                capture.Changed+=active => {
                    if(active) SetStatus("Controlling the Legion Go — Ctrl + Alt + Esc brings you back.");
                    else ShowConnectedStatus();
                    tray.Text=active ? "DeskBridge — controlling Legion Go" : "DeskBridge — local control";
                };
                if(remember.Checked) {
                    try { SavedConnection.SaveSender(pairingCode); }
                    catch { capture.Dispose(); capture=null; paired.Dispose(); throw; }
                }
                Attach(paired); capture.Connection=paired;
                SetStatus("Connected — waiting for the Legion Go screen size…");
                code.Clear();
            }
        } catch(Exception ex) {
            if(session==current) StopSession(current.IsCancellationRequested ? "Disconnected." : (ex is OperationCanceledException ? "Connection timed out. Check that the receiver is running and its address and firewall allow the connection." : "Could not connect: "+ex.Message)+(reconnectWanted ? " Retrying in 15 seconds�" : ""));
        }
    }
    void Attach(Connection paired)
    {
        connection=paired;
        var target=receiving ? sink=new InputSink() : null;
        if(target!=null) target.CursorChanged+=p=>paired.Send(p);
        bool isReceiver=receiving;
        paired.Received+=p => {
            if(isReceiver) {
                if(p.Kind==Kind.Hello) {
                    UI(() => {
                        if(connection!=paired) return;
                        peerSupportsDisplays=p.C>=4; SendDimensions();
                    });
                    return;
                }
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
                capture?.SetRemoteSize(p.A,p.B,p.C);
                incomingDisplays.Clear(); expectedDisplays=p.C>=4 && p.D is >0 and <=32 ? p.D : 0;
                if(expectedDisplays==0) capture?.SetRemoteScreens(Array.Empty<Rectangle>());
                ShowConnectedStatus();
            });
            else if(p.Kind==Kind.Display && !isReceiver) UI(() => {
                if(connection!=paired || expectedDisplays==0 || capture==null) return;
                var size=capture.RemoteSize;
                if(p.A<0 || p.B<0 || p.C<32 || p.D<32 || (long)p.A+p.C>size.Width || (long)p.B+p.D>size.Height) {
                    expectedDisplays=0; incomingDisplays.Clear(); return;
                }
                incomingDisplays.Add(new Rectangle(p.A,p.B,p.C,p.D));
                if(incomingDisplays.Count==expectedDisplays) {
                    var detected=incomingDisplays.ToArray();
                    capture.SetRemoteScreens(detected); expectedDisplays=0;
                    if(!preferences.Layout.RemoteScreens.SequenceEqual(detected)) {
                        preferences=preferences with { Layout=preferences.Layout with { RemoteScreens=detected } };
                        try { preferences.Save(); } catch(Exception ex) { SetStatus("Could not save detected monitors: "+ex.Message); }
                    }
                    layoutSummary.Text=preferences.Layout+ $" · {incomingDisplays.Count} remote monitor(s)";
                }
            });
            else if(p.Kind==Kind.Cursor && !isReceiver) UI(() => {
                if(connection==paired) capture?.SetRemoteCursor(p.A,p.B,p.D==1);
            });
        };
        paired.Closed+=() => {
            target?.Close();
            UI(() => { if(connection==paired) StopSession(reconnectWanted ? "Connection ended. Control is local. Reconnecting in 15 seconds�" : "Connection ended. Control is local. Connect again to resume."); });
        };
        paired.Start();
        if(receiving) { SendDimensions(); dimensions.Start(); }
        else paired.Send(new Packet(Kind.Hello,C:4));
    }
    void SendDimensions()
    {
        if(!receiving || connection?.Alive!=true) return;
        var b=SystemInformation.VirtualScreen;
        var screens=Screen.AllScreens.OrderByDescending(s=>s.Primary).ThenBy(s=>s.DeviceName,StringComparer.Ordinal).Take(32).ToArray();
        connection.Send(new Packet(Kind.Hello,b.Width,b.Height,4,peerSupportsDisplays ? screens.Length : 0));
        if(peerSupportsDisplays) foreach(var screen in screens) {
            var r=screen.Bounds;
            connection.Send(new Packet(Kind.Display,r.Left-b.Left,r.Top-b.Top,r.Width,r.Height));
        }
    }
    void StopSession(string text)
    {
        var previous=session; session=null;
        peerSupportsDisplays=false; expectedDisplays=0; incomingDisplays.Clear();
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
        if(m.Msg==0x312 && m.WParam.ToInt32()==1) { reconnectWanted=false; StopSession("Receiving stopped. Create a new pairing code to reconnect."); Open(); }
        base.WndProc(ref m);
    }
}
