# DeskBridge · Windows preview 0.1.3

One keyboard and mouse for your Windows PC and Lenovo Legion Go.

[Download the latest Windows release](https://github.com/calinadrian/DeskBridge/releases/latest) · [Source repository](https://github.com/calinadrian/DeskBridge)

## Start on the Legion Go

1. Extract the downloaded ZIP, copy its folder to the Legion Go, and open `DeskBridge.exe`.
2. Choose **Receive control on this device**, then **Create pairing code**.
3. If Windows asks about network access, allow it on your **private/home network**.
4. Keep the app open. It shows this device's address and a private pairing code.

## Connect from the PC

1. Open the same `DeskBridge.exe` on the PC with your keyboard and mouse.
2. Leave **Share this keyboard and mouse** selected.
3. Enter the Legion Go's address, then paste its complete pairing code. You can transfer the code in a text file using a USB drive or a shared note you control. It is intentionally long to protect access to the device.
4. Click **Connect**, then **Arrange monitors…** to match your desk.
5. Move the pointer away from the screen edge first, then move it to the selected edge to switch devices. Or press **Ctrl + Alt + Space**.

Both devices must be on the same network. Ethernet and Wi-Fi can be mixed on the same router. Guest Wi-Fi may prevent devices from communicating.

## Arrange monitors

On the sharing PC, click **Arrange monitors…**. Drag the Legion Go tile to the left, right, above, or below your selected local monitor. The edges snap together. Drag along the edge to adjust alignment, or use the Position and Alignment controls. The highlighted seam shows where the pointer can cross in either direction; parts of the edge without a touching screen do not switch devices.

If this PC has multiple monitors, choose which local monitor should connect to the Legion Go. Its existing Windows display layout stays in place. The Legion Go's desktop is one tile, including any displays attached to it. Connect first to use its actual desktop size in the preview. Click **Save layout** to keep the layout between launches. Changing layout returns control locally before applying it.

## Mouse jumping in games

- **Playing on this PC:** **Pause edge switching in fullscreen apps and games** is on by default. A fullscreen game cannot automatically send your pointer to the other device. For windowed games or complete protection, enable **Lock control to this PC (for games)**. This also blocks the switching shortcut until you uncheck it.
- **Playing on the Legion Go with this PC's mouse:** enable **Relative mouse for remote games** on the sharing PC. This sends movement deltas instead of moving the remote pointer to absolute desktop coordinates. Both devices need v0.1.2 or newer; the app will ask you to update an older receiver rather than send it incompatible movement.
- Relative mode turns off automatic edge switching in both directions. Press **Ctrl + Alt + Space** to share input and **Ctrl + Alt + Esc** to return locally. Entering game mode does not warp the remote cursor to the middle of the screen.
- Games differ in whether they accept Windows-generated input. These changes have been compiled, but game behavior and pointer movement on the two devices have not been validated.

In v0.1.3, normal sharing between two updated devices also sends relative mouse movement and receives the actual cursor position back from the receiving device. This prevents a stale absolute position from moving the cursor back after a game restores it when you release left or right click. Absolute positioning is used when initially crossing into the other desktop; relative game mode skips that initial repositioning as well. Older receivers retain the original normal-movement behavior, so update both devices for this fix.

With fullscreen protection enabled, a receiving fullscreen game also pauses automatic switching back to this PC. Use the return shortcut when you finish playing.

While any mouse button is held, automatic switching stays on the current device. After release, move the pointer at least 24 pixels away from the joining edge before crossing it again. Releasing a drag at an edge alone does not switch devices.

Layout and mouse options are saved locally in `%LOCALAPPDATA%\DeskBridge\settings.json`. Pairing codes are not saved.

## Updates from GitHub

DeskBridge checks this repository's latest stable GitHub Release when it opens and every six hours while running. You can also click **Check for updates**. Checks do not interrupt a connection and never install silently.

When a new version is available, click **Update & restart**. The app downloads the Windows executable over HTTPS, verifies its SHA-256 checksum and file version, disconnects safely, replaces itself, and restarts. Keep its filename as `DeskBridge.exe` and keep the folder in a writable location such as Documents. Windows may block replacing an app that another instance is using; close the other instance before updating.

The previous executable is kept beside the app as `DeskBridge.exe.previous`. If you need to recover it, close DeskBridge, rename the current executable to `DeskBridge-new.exe`, and rename `DeskBridge.exe.previous` to `DeskBridge.exe`. If the updater cannot launch the new executable, it restores the previous one. This does not detect problems that occur after the new app has launched.

Each device updates separately. Updating disconnects the pair; open Receive control and pair again afterwards. You do not need Git or a GitHub account on either device. No update will be offered until there is a newer stable release. Old update downloads are removed after two days when the app starts again.

## Switching and stopping

- **Ctrl + Alt + Space** on the sharing PC: switch control between the devices.
- **Ctrl + Alt + Esc** on the sharing PC: return control to this PC.
- Moving back across the joining screen edge returns control to the PC. Edge switching waits until keys and mouse buttons are released, so dragging stays on the selected device. Use the shortcut for dragging between screen edges on one device.
- **Ctrl + Alt + Esc** on the receiving device's own keyboard: stop receiving and disconnect.
- **Disconnect** in the window or tray menu ends a connection.
- Minimize hides the window in the system tray. Double-click the tray icon to reopen it. Closing the window exits the app.
- Losing the connection returns local control and releases keys/buttons on the receiving device. The heartbeat detects an unresponsive connection in about four seconds.

## If the connection is blocked

Check the address, that both apps are open, and that the receiving device's network is marked **Private** in Windows settings. Allow DeskBridge through Windows Defender Firewall on private networks only.

The optional `Allow-Home-Network.ps1` file creates one inbound TCP rule for this exact app path, restricted to the private profile and local subnet. Put it next to `DeskBridge.exe` and run it with PowerShell as administrator **on the receiving device only** if needed. Moving the app afterwards requires creating the rule again. It does not change your network's profile or disable the firewall.

## Preview limits

- Windows 10/11 x64 only. Both devices need to be signed in to a normal desktop.
- This build was compiled and packaged; live use across two devices has not been tested.
- Keyboard, pointer movement, left/right/middle/back/forward buttons, and vertical/horizontal scrolling are implemented. Clipboard, file transfer, automatic discovery, automatic reconnection, and startup at sign-in are not included.
- The receiving device's whole Windows desktop is treated as one rectangle, including multiple displays. Pointer speed follows Windows pointer settings. A selected local monitor can connect on any side; remote physical monitors are arranged within Windows settings on that device.
- Screen resolution changes are sent every two seconds. The app does not transmit video; each device keeps its own screen.
- Normal Windows desktop apps are the intended use. Games with raw/relative mouse input, anti-cheat restrictions, and different keyboard layouts may need further work. No attempt is made to bypass game restrictions.
- Windows UAC's secure desktop, sign-in, Ctrl + Alt + Delete, and lock-screen control are not supported. An app running as administrator may block injected input from DeskBridge running normally. Use the same permissions as your target app only when needed.
- Keyboard and mouse data goes directly between the devices; it is not uploaded. There is no installer, background service, telemetry, or input recording. Update checks contact GitHub and identify the app version. Pairing uses a fresh certificate and 256-bit secret every time you start receiving. TLS validates the certificate fingerprint embedded in the pairing code. The app accepts one paired sender; codes are cleared from its window after pairing. A copied code remains in your clipboard until you replace it.
- DeskBridge is unsigned; Windows may show its usual warning for an unfamiliar downloaded app.

## Source and rebuilding

The complete C# source is in `Source`. With the .NET 8 SDK installed:

```powershell
dotnet publish Source/DeskBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o App
```

The executable includes its runtime. No separate .NET installation is needed on either device.

For complete release assets, run `./scripts/publish.ps1` in PowerShell. To publish a new update, bump `<Version>` in `Source/DeskBridge.csproj`, commit and push it, then push the matching tag (for example `v0.1.2`). The GitHub Actions release workflow builds the executable, checksum, and portable ZIP and publishes the release. The tag must match the app version. Only stable releases are offered by the updater.

Update assets are `DeskBridge-win-x64.exe` and `DeskBridge-win-x64.exe.sha256`. The checksum protects download integrity; it is not a digital signature. Repository publishing access controls who can ship updates.

Implementation references: [Windows keyboard hooks](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [Windows SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [.NET TLS streams](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream).
