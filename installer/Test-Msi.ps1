param(
    [Parameter(Mandatory)] [string]$Package,
    [Parameter(Mandatory)] [string]$Application
)
$ErrorActionPreference = 'Stop'
$Package = (Resolve-Path -LiteralPath $Package).Path
$expectedHash = (Get-FileHash -LiteralPath $Application -Algorithm SHA256).Hash
$target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\DeskBridge'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskBridge.lnk'
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'

function Find-InstalledDeskBridge {
    Get-ItemProperty -Path "$uninstallRoot\*" -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'DeskBridge'
}

function Invoke-Installer([string]$Action) {
    $log = Join-Path ([IO.Path]::GetTempPath()) ("DeskBridge-MSI-" + [guid]::NewGuid().ToString('N') + '.log')
    $process = Start-Process msiexec.exe -ArgumentList "$Action `"$Package`" /qn /norestart /L*v `"$log`"" -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 100 }
        throw "Windows Installer returned $($process.ExitCode)."
    }
    Remove-Item -LiteralPath $log
}

if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $shortcut) -or (Find-InstalledDeskBridge)) {
    throw 'Run the installer test on a Windows account with no existing DeskBridge installation.'
}

Invoke-Installer /i
try {
    $exe = Join-Path $target 'DeskBridge.exe'
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'Installed application hash does not match the release executable.'
    }
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'The Start menu shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'The shortcut points to the wrong executable.' }
    if (!(Find-InstalledDeskBridge)) { throw 'DeskBridge is missing from Installed apps.' }
} finally {
    Invoke-Installer /x
}
if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $shortcut) -or (Find-InstalledDeskBridge)) {
    throw 'Uninstall left installed application files or registration behind.'
}
Write-Host 'DeskBridge installation and removal passed.'
