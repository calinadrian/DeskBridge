using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskBridge;

internal sealed class MainForm : Form
{
    readonly RadioButton share=new DashboardRadioButton() { Text="Share this keyboard and mouse", Checked=true, AutoSize=true };
    readonly RadioButton receive=new DashboardRadioButton() { Text="Receive control on this device", AutoSize=true };
    readonly TextBox address=new() { PlaceholderText="For example, 192.168.1.42", Dock=DockStyle.Fill };
    readonly TextBox code=new() { Multiline=true, Height=76, Dock=DockStyle.Fill, ScrollBars=ScrollBars.None };
    readonly Button arrange=new DashboardButton() { Text="Arrange monitors\u2026", AutoSize=true };
    readonly Label layoutSummary=new() { AutoSize=true, MaximumSize=new Size(580,0) };
    readonly CheckBox edges=new DashboardCheckBox() { Text="Switch when the pointer reaches the screen edge", Checked=true, AutoSize=true };
    readonly CheckBox fullscreen=new DashboardCheckBox() { Text="Pause edge switching in fullscreen apps and games", Checked=true, AutoSize=true };
    readonly CheckBox lockLocal=new DashboardCheckBox() { Text="Lock control to this PC (for games)", AutoSize=true };
    readonly CheckBox relative=new DashboardCheckBox() { Text="Relative mouse for remote games \u2014 switch with shortcut only", AutoSize=true };
    readonly CheckBox remember=new DashboardCheckBox() { Text="Remember this connection on this Windows account",AutoSize=true };
    readonly CheckBox automatic=new DashboardCheckBox() { Text="Connect automatically when DeskBridge opens (retry if offline)",AutoSize=true };
    readonly CheckBox startup=new DashboardCheckBox() { Text="Start DeskBridge with Windows",AutoSize=true };
    readonly Button forget=new DashboardButton() { Text="Forget saved pairing",AutoSize=true };
    readonly System.Windows.Forms.Timer reconnect=new() { Interval=15000 };
    bool reconnectWanted,changingStartup;
    Preferences preferences=Preferences.Load();
    readonly Button action=new DashboardButton() { Text="Connect", AutoSize=true };
    readonly Button stop=new DashboardButton() { Text="Disconnect", AutoSize=true, Enabled=false };
    readonly Button copy=new DashboardButton() { Text="Copy pairing code", AutoSize=true, Enabled=false };
    readonly Button local=new DashboardButton() { Text="Return control here", AutoSize=true, Enabled=false };
    readonly Label status=new() { Text="Ready to connect", AutoSize=true, ForeColor=Theme.Text, MaximumSize=new Size(590,0) };
    readonly Label help=new() { AutoSize=true, MaximumSize=new Size(590,0) };
    readonly Label ips=new() { AutoSize=true, MaximumSize=new Size(590,0), ForeColor=Theme.Text };
    readonly Panel senderPanel=new() { AutoSize=true, Dock=DockStyle.Top };
    readonly Label sessionState=new() { AutoSize=true };
    readonly Label pairingLabel=new() { Text="Pairing code from the receiving device",AutoSize=true };
    readonly Label savedPairing=new() { Text="Saved pairing is ready. Click Connect, or paste a different code below.",AutoSize=true,Visible=false };
    readonly LinkLabel downloads=new() { Text="Open download page",AutoSize=true,LinkColor=Theme.Accent,ActiveLinkColor=Theme.Text,VisitedLinkColor=Theme.Accent };
    readonly NotifyIcon tray=new() { Icon=SystemIcons.Application, Text="DeskBridge \u2014 local control", Visible=true };
    readonly System.Windows.Forms.Timer dimensions=new() { Interval=2000 };
    readonly System.Windows.Forms.Timer updateTimer=new() { Interval=6*60*60*1000 };
    readonly Button updateButton=new DashboardButton() { Text="Check for updates", AutoSize=true };
    readonly Label updateStatus=new() { AutoSize=true, MaximumSize=new Size(590,0), ForeColor=Theme.Muted };
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
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        Text = "DeskBridge " + Updater.DisplayVersion;
        ClientSize = new Size(820, 670);
        MinimumSize = new Size(650, 550);
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);

        var root = new Panel { Dock = DockStyle.Fill };
        Controls.Add(root);

        var navigation = new FlowLayoutPanel {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Theme.Surface, Padding = new Padding(20, 8, 16, 8), Margin = Padding.Empty
        };
        var title = new Label {
            Text = "DeskBridge", AutoSize = true, ForeColor = Theme.Accent,
            Font = new Font("Segoe UI", 14, FontStyle.Bold), Margin = new Padding(0, 4, 18, 0)
        };
        navigation.Controls.Add(title);
        var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        root.Controls.Add(pages);
        var sections = new List<(Button Button, Panel Page)>();

        void SelectPage(Panel selected)
        {
            foreach (var (button, page) in sections)
            {
                bool active = page == selected;
                page.Visible = active;
                button.ForeColor = active ? Theme.Accent : Theme.Text;
                button.BackColor = active ? Theme.Field : Theme.Surface;
            }
        }

        TableLayoutPanel AddPage(string name)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
            var content = new TableLayoutPanel {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Padding = new Padding(24, 18, 24, 16)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(content);
            pages.Controls.Add(page);
            var button = new Button {
                Text = name, UseMnemonic = false, AutoSize = true, MinimumSize = new Size(0, 34),
                Margin = new Padding(0, 0, 5, 0)
            };
            button.Click += (_, _) => SelectPage(page);
            navigation.Controls.Add(button);
            sections.Add((button, page));
            Add(content, new Label {
                Text = name, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI", 21, FontStyle.Bold)
            });
            return content;
        }

        void Add(TableLayoutPanel panel, Control control, int gap = 8)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 0, 0, gap);
            panel.Controls.Add(control, 0, row);
        }

        Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, UseMnemonic = false };

        TableLayoutPanel Card(TableLayoutPanel page, string heading)
        {
            var card = new TableLayoutPanel {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Surface,
                Padding = new Padding(16, 12, 16, 8)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(card, new Label { Text = heading, AutoSize = true, Font = new Font("Segoe UI Semibold", 11) });
            Add(page, card, 12);
            return card;
        }

        var connectPage = AddPage("Connection");
        var inputPage = AddPage("Monitors & input");
        var settingsPage = AddPage("Settings & updates");

        var pairing = Card(connectPage, "Device pairing");
        share.Text = "Share input";
        receive.Text = "Receive input";
        var roles = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        share.Margin = new Padding(0, 0, 22, 0);
        receive.Margin = Padding.Empty;
        roles.Controls.AddRange(new Control[] { share, receive });
        Add(pairing, roles);
        help.ForeColor = Theme.Muted;
        Add(pairing, help, 12);
        var addressFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        addressFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(addressFields, new Label { Text = "Receiving device address", AutoSize = true }, 4);
        Add(addressFields, address, 0);
        senderPanel.Controls.Add(addressFields);
        senderPanel.Dock = DockStyle.Fill;
        Add(pairing, senderPanel);
        Add(pairing, ips);
        ips.Visible = false;
        ips.TextChanged += (_, _) => ips.Visible = ips.Text.Length != 0;
        savedPairing.ForeColor = Theme.Connected;
        Add(pairing, savedPairing);
        Add(pairing, pairingLabel, 4);
        code.Height = 58;
        code.Font = new Font("Consolas", 10);
        Add(pairing, code);
        Add(pairing, copy);
        Add(pairing, Hint("Keep both devices on the same local network."));

        var displays = Card(inputPage, "Monitor layout");
        layoutSummary.Text = preferences.Layout.ToString();
        Add(displays, layoutSummary);
        Add(displays, arrange);
        Add(displays, Hint("Connect to detect remote displays, then arrange them to match your desk."));
        var switching = Card(inputPage, "Input switching");
        edges.Checked = preferences.EdgeEnabled;
        fullscreen.Checked = preferences.PauseFullscreen;
        relative.Checked = preferences.RelativeMouse;
        lockLocal.Checked = preferences.LockLocal;
        edges.Text = "Switch at touching monitor edges";
        lockLocal.Text = "Keep control on this PC";
        relative.Text = "Remote game mode (switch with keyboard shortcuts)";
        foreach (var option in new[] { edges, fullscreen, lockLocal, relative })
            Add(switching, option);
        Add(switching, Hint("Ctrl + Alt + Space   Switch devices\nCtrl + Alt + Esc       Return here or stop receiving"));

        var preferencesCard = Card(settingsPage, "Connection preferences");
        remember.Checked = preferences.RememberConnection;
        automatic.Checked = preferences.AutoConnect;
        startup.Checked = preferences.StartWithWindows;
        automatic.Enabled = remember.Checked;
        remember.Text = "Remember this connection";
        automatic.Text = "Connect automatically and retry when offline";
        foreach (var option in new[] { remember, automatic, startup })
            Add(preferencesCard, option);
        Add(preferencesCard, forget);
        Add(preferencesCard, Hint("Saved pairing is encrypted for your Windows account."));
        var updatesCard = Card(settingsPage, "Software updates");
        updateStatus.Text = "Version " + Updater.DisplayVersion;
        Add(updatesCard, updateStatus);
        var updates = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        downloads.Margin = new Padding(12, 9, 0, 0);
        updates.Controls.AddRange(new Control[] { updateButton, downloads });
        Add(updatesCard, updates);
        Add(settingsPage, Hint("Minimize to keep DeskBridge in the tray. Closing the window exits."));

        var footer = new TableLayoutPanel {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Surface,
            Padding = new Padding(24, 10, 24, 6), Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        action.MinimumSize = new Size(130, 36);
        stop.MinimumSize = new Size(110, 36);
        local.MinimumSize = new Size(140, 36);
        action.Margin = stop.Margin = local.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.AddRange(new Control[] { action, stop, local });
        sessionState.Font = new Font("Segoe UI Semibold", 10);
        Add(footer, sessionState, 2);
        Add(footer, status, 9);
        Add(footer, buttons, 6);
        root.Controls.Add(footer);
        root.Controls.Add(navigation);

        void FitText()
        {
            int width = Math.Max(220, pages.ClientSize.Width - LogicalToDeviceUnits(104));
            foreach (Control page in pages.Controls)
            {
                foreach (var label in DescendantLabels(page))
                    label.MaximumSize = new Size(width, 0);
            }
            status.MaximumSize = new Size(Math.Max(220, root.ClientSize.Width - LogicalToDeviceUnits(48)), 0);
        }

        pages.SizeChanged += (_, _) => FitText();
        FitText();
        Theme.Style(root, action, updateButton);
        foreach (var (button, _) in sections)
            button.FlatAppearance.BorderSize = 0;
        SelectPage(sections[0].Page);
        SetStatus("Ready to connect");
        downloads.LinkClicked+=(_,_)=> {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/"+Updater.Repository+"/releases/latest") { UseShellExecute=true }); }
            catch(Exception ex) { updateStatus.Text="Could not open the download page: "+ex.Message; }
        };
        address.Text=preferences.Address;
        receive.Checked=preferences.ReceiveMode; share.Checked=!preferences.ReceiveMode;
        share.CheckedChanged+=(_,_) => { if(share.Checked) { Mode(); PersistConnectionOptions(); } };
        receive.CheckedChanged+=(_,_) => { if(receive.Checked) { Mode(); PersistConnectionOptions(); } };
        address.TextChanged+=(_,_) => PersistConnectionOptions();
        remember.CheckedChanged+=(_,_) => {
            automatic.Enabled=remember.Checked;
            if(!remember.Checked) {
                automatic.Checked=false; reconnectWanted=false; savedPairing.Visible=false;
                RefreshSessionState();
                try { SavedConnection.Forget(); } catch(Exception ex) { SetStatus("Could not forget pairing: "+ex.Message); }
            }
            PersistConnectionOptions();
        };
        automatic.CheckedChanged+=(_,_) => { reconnectWanted=automatic.Checked; stop.Enabled=session!=null || reconnectWanted; RefreshSessionState(); PersistConnectionOptions(); };
        startup.CheckedChanged+=(_,_) => {
            if(changingStartup) return;
            try { SavedConnection.SetStartup(startup.Checked); PersistConnectionOptions(); }
            catch(Exception ex) { changingStartup=true; startup.Checked=preferences.StartWithWindows; changingStartup=false; SetStatus("Could not change Windows startup: "+ex.Message); }
        };
        forget.Click+=(_,_) => {
            reconnectWanted=false; automatic.Checked=false; StopSession("Saved pairing forgotten. Create a new code on the receiving device.");
            try { SavedConnection.Forget(); code.Clear(); savedPairing.Visible=false; } catch(Exception ex) { SetStatus("Could not forget pairing: "+ex.Message); }
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
            RefreshSessionState();
            if(startupLaunch) { WindowState=FormWindowState.Minimized; Hide(); }
            if(reconnectWanted) _=BeginSession();
            await CheckUpdate();
        };
        Mode();
    }
    static IEnumerable<Label> DescendantLabels(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label label) yield return label;
            foreach (var nested in DescendantLabels(child)) yield return nested;
        }
    }

    void Mode()
    {
        senderPanel.Visible=share.Checked; copy.Visible=receive.Checked; code.ReadOnly=receive.Checked;
        edges.Enabled=fullscreen.Enabled=lockLocal.Enabled=relative.Enabled=share.Checked;
        arrange.Enabled=share.Checked && preferences.Layout.RemoteScreens.Length>0;
        code.Clear(); ips.Text=""; savedPairing.Visible=false;
        if (session == null) SetStatus(share.Checked ? "Ready to connect" : "Ready to receive");
        if(share.Checked && preferences.RememberConnection) {
            try { savedPairing.Visible=!string.IsNullOrWhiteSpace(SavedConnection.SenderCode()); }
            catch { SetStatus("Saved pairing could not be read. Paste a new code, or use Forget saved pairing in Settings."); }
        }
        code.PlaceholderText=share.Checked ? "Paste a code to pair a new device. Leave blank to use the saved pairing." : "Click Start receiving to show this device's pairing code.";
        pairingLabel.Text=share.Checked ? "Pairing code from the receiving device" : "This device's private pairing code";
        action.Text=share.Checked ? "Connect" : "Start receiving";
        stop.Text=share.Checked ? "Disconnect" : "Stop receiving";
        help.Text=share.Checked ? "Control the other device with this PC's keyboard and mouse."
            : "Receive keyboard and mouse input from your other PC. Start receiving, then copy the code to that PC.";
    }
    void PersistConnectionOptions()
    {
        preferences=preferences with { ReceiveMode=receive.Checked,Address=address.Text.Trim(),RememberConnection=remember.Checked,
            AutoConnect=automatic.Checked && remember.Checked,StartWithWindows=startup.Checked };
        try { preferences.Save(); } catch(Exception ex) { SetStatus("Could not save connection options: "+ex.Message); }
    }
    void Busy(bool busy)
    {
        share.Enabled=receive.Enabled=remember.Enabled=!busy; address.ReadOnly=busy; action.Enabled=!busy;
        code.ReadOnly=busy || receive.Checked; stop.Text=receive.Checked ? "Stop receiving" : "Disconnect"; stop.Enabled=busy || reconnectWanted; copy.Enabled=busy && receiving;
        local.Enabled=busy && !receiving && connection?.Alive==true;
    }
    void SetStatus(string text)
    {
        status.Text = text;
        RefreshSessionState();
    }

    void RefreshSessionState()
    {
        bool connected = connection?.Alive == true;
        sessionState.Text = connected ? "Connected"
            : session != null ? (receiving ? "Waiting for a device" : "Connecting")
            : reconnectWanted ? "Waiting to retry" : "Disconnected";
        sessionState.ForeColor = connected ? Theme.Connected : session != null || reconnectWanted ? Theme.Accent : Theme.Muted;
    }
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
        arrange.Enabled=share.Checked && (capture?.RemoteReady==true || preferences.Layout.RemoteScreens.Length>0);
        if(capture?.Active==true || connection?.Alive!=true) return;
        if(lockLocal.Checked) SetStatus("Connected \u2014 control is locked to this PC. Uncheck the lock to switch devices.");
        else if(relative.Checked && capture?.RemoteProtocol<2) SetStatus("Relative game mode needs v0.1.2 or newer on the receiving device. Update both devices.");
        else if(relative.Checked) SetStatus("Connected \u2014 game mode. Ctrl + Alt + Space shares input; Ctrl + Alt + Esc returns here.");
        else SetStatus("Connected \u2014 move across the touching edge or press Ctrl + Alt + Space.");
    }
    async Task CheckUpdate()
    {
        if(updateBusy || lifetime.IsCancellationRequested) return;
        updateBusy=true; updateButton.Enabled=false;
        updateStatus.Text="Checking GitHub for updates\u2026";
        try {
            availableUpdate=await Updater.Check(lifetime.Token);
            if(lifetime.IsCancellationRequested) return;
            updateStatus.Text=availableUpdate==null ? "Version "+Updater.DisplayVersion+" \u00b7 You're up to date."
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
        updateStatus.Text="Downloading update\u2026";
        try {
            var progress=new Progress<int>(percent => {
                if(!lifetime.IsCancellationRequested) updateStatus.Text=percent<0 ? "Downloading update\u2026" : $"Downloading update\u2026 {percent}%";
            });
            var job=await Updater.Download(availableUpdate,progress,lifetime.Token);
            if(lifetime.IsCancellationRequested) return;
            StopSession("Installing update\u2026");
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
        var requestedCode=code.Text.Trim();
        if(!receiving && string.IsNullOrWhiteSpace(requestedCode) && remember.Checked) {
            try { requestedCode=SavedConnection.SenderCode(); }
            catch { reconnectWanted=false; stop.Enabled=false; SetStatus("Saved pairing cannot be read. Paste a new code, or use Forget saved pairing in Settings."); return; }
        }
        if(!receiving && (string.IsNullOrWhiteSpace(address.Text)||string.IsNullOrWhiteSpace(requestedCode))) {
            reconnectWanted=false; stop.Enabled=false; SetStatus("Enter the receiving device address and pairing code first."); return;
        }
        var current=session=new CancellationTokenSource();
        bool networkStarted=false;
        Busy(true);
        try {
            if(receiving) {
                if(!Native.RegisterHotKey(Handle,1,0x4003,0x1B))
                    throw new InvalidOperationException("Ctrl + Alt + Esc is already in use. Close the other app using it, then try again.");
                var identity=SavedConnection.Receiver(remember.Checked);
                using var cert=identity.Certificate;
                var secret=identity.Secret;
                listener=new TcpListener(IPAddress.Any,Connection.Port); listener.Start(1); networkStarted=true;
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
                SetStatus("Connecting securely\u2026");
                var pairingCode=requestedCode;
                networkStarted=true;
                var paired=await Connection.Connect(address.Text,pairingCode,current.Token);
                if(session!=current || current.IsCancellationRequested) { paired.Dispose(); return; }
                try { capture=new InputCapture {
                    Layout=preferences.Layout with { RemoteScreens=Array.Empty<Rectangle>() }, EdgeEnabled=edges.Checked, PauseFullscreen=fullscreen.Checked,
                    RelativeMouse=relative.Checked, LockLocal=lockLocal.Checked }; }
                catch { paired.Dispose(); throw; }
                capture.Changed+=active => {
                    if(active) SetStatus("Controlling the other device \u2014 Ctrl + Alt + Esc brings you back.");
                    else ShowConnectedStatus();
                    tray.Text=active ? "DeskBridge \u2014 controlling the other device" : "DeskBridge \u2014 local control";
                };
                if(remember.Checked) {
                    networkStarted=false;
                    try { SavedConnection.SaveSender(pairingCode); }
                    catch { capture.Dispose(); capture=null; paired.Dispose(); throw; }
                }
                Attach(paired); capture.Connection=paired; local.Enabled=true;
                SetStatus("Connected \u2014 detecting remote displays\u2026");
                code.Clear(); savedPairing.Visible=remember.Checked;
            }
        } catch(Exception ex) {
            if(session!=current) return;
            bool retryable=networkStarted && ex is (OperationCanceledException or System.Net.Sockets.SocketException or IOException);
            if(!retryable) reconnectWanted=false;
            string message=current.IsCancellationRequested ? "Disconnected." : ex switch {
                CryptographicException => "Could not open or save the pairing key. Use Forget saved pairing in Settings, then start receiving again. " + ex.Message,
                System.Net.Sockets.SocketException => "Network connection failed. Check the receiver address and that only one copy is receiving. " + ex.Message,
                System.ComponentModel.Win32Exception => "Windows could not save or open the pairing details. " + ex.Message,
                OperationCanceledException => "Connection timed out. Check that the receiver is running and its address and firewall allow the connection.",
                _ => (receiving ? "Could not start receiving: " : "Could not connect: ")+ex.Message
            };
            StopSession(message+(reconnectWanted ? " Retrying in 15 seconds..." : ""));
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
                        tray.Text=active ? "DeskBridge \u2014 receiving control" : "DeskBridge \u2014 connected, idle";
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
                    arrange.Enabled=share.Checked;
                    layoutSummary.Text=preferences.Layout+ $" \u00b7 {incomingDisplays.Count} remote monitor(s)";
                }
            });
            else if(p.Kind==Kind.Cursor && !isReceiver) UI(() => {
                if(connection==paired) capture?.SetRemoteCursor(p.A,p.B,p.D==1);
            });
        };
        paired.Closed+=() => {
            target?.Close();
            UI(() => { if(connection==paired) StopSession(reconnectWanted ? "Connection ended. Control is local. Reconnecting in 15 seconds..." : "Connection ended. Control is local. Connect again to resume."); });
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
        arrange.Enabled=share.Checked && preferences.Layout.RemoteScreens.Length>0;
        tray.Text="DeskBridge \u2014 local control"; SetStatus(text);
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
