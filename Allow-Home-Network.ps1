# Optional: place next to DeskBridge.exe and run in an administrator PowerShell.
# Adds a private-network rule limited to local-subnet devices.
$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot 'DeskBridge.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    $appPath = Join-Path $PSScriptRoot 'App\DeskBridge.exe'
}
if (-not (Test-Path -LiteralPath $appPath)) {
    throw 'Place this script beside DeskBridge.exe, then run it again.'
}
$appPath = (Resolve-Path -LiteralPath $appPath).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as administrator to add the optional firewall rule.'
}
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $pathHash = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($appPath))
    $hashText = [BitConverter]::ToString($pathHash).Replace('-', '')
    $ruleName = 'DeskBridge-Home-' + $hashText.Substring(0,12)
} finally {
    $hasher.Dispose()
}
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName 'DeskBridge — home network' `
        -Direction Inbound -Action Allow -Program $appPath -Protocol TCP -LocalPort 45839 `
        -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
Write-Host 'DeskBridge is allowed on private local networks.'
Write-Host "To remove this rule: Remove-NetFirewallRule -Name '$ruleName'"
Read-Host 'Press Enter to close'
