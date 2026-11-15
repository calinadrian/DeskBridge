param(
    [Parameter(Mandatory)] [string]$Application,
    [Parameter(Mandatory)] [version]$Version,
    [Parameter(Mandatory)] [string]$Output
)
$ErrorActionPreference = 'Stop'
$Application = (Resolve-Path -LiteralPath $Application).Path
$Output = [IO.Path]::GetFullPath($Output)
$fullVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Application).FileVersion
$productVersion = $Version.ToString(3)
if ([version]$fullVersion -ne [version]::new($Version.Major, $Version.Minor, $Version.Build, 0)) {
    throw "Installer version $productVersion does not match executable version $fullVersion."
}

# Keep component identities and the upgrade code stable across releases.
$upgradeCode = '{284FBF95-8B32-48C1-A4AE-FCF38E3D80C6}'
$sha = [Security.Cryptography.SHA256]::Create()
try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$upgradeCode/$productVersion")) }
finally { $sha.Dispose() }
$productCode = ([guid]::new([byte[]]$bytes[0..15])).ToString('B').ToUpperInvariant()
$packageCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
$work = Join-Path ([IO.Path]::GetTempPath()) ("DeskBridge-MSI-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class InstallerDatabase
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiOpenDatabase(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")]
    public static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")]
    public static extern uint MsiCreateRecord(uint fields);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiRecordSetString(uint record, uint field, string value);
    [DllImport("msi.dll")]
    public static extern uint MsiRecordSetInteger(uint record, uint field, int value);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiRecordSetStream(uint record, uint field, string path);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiGetSummaryInformation(uint database, string path, uint count, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    public static extern uint MsiSummaryInfoSetProperty(uint summary, uint field, uint type, int number, IntPtr time, string text);
    [DllImport("msi.dll")]
    public static extern uint MsiSummaryInfoPersist(uint summary);
    [DllImport("msi.dll")]
    public static extern uint MsiDatabaseCommit(uint database);
    [DllImport("msi.dll")]
    public static extern uint MsiCloseHandle(uint handle);
}
"@

function Assert-Msi([uint32]$Code, [string]$Operation) {
    if ($Code -ne 0) { throw "$Operation failed with Windows Installer error $Code." }
}

function Invoke-Sql([string]$Sql, [uint32]$Record = 0) {
    [uint32]$view = 0
    Assert-Msi ([InstallerDatabase]::MsiDatabaseOpenView($database, $Sql, [ref]$view)) $Sql
    try { Assert-Msi ([InstallerDatabase]::MsiViewExecute($view, $Record)) $Sql }
    finally { $null = [InstallerDatabase]::MsiCloseHandle($view) }
}

function Add-Row([string]$Table, [object[]]$Values) {
    $record = [InstallerDatabase]::MsiCreateRecord($Values.Length)
    if (!$record) { throw "Could not create MSI record for $Table." }
    try {
        for ($i = 0; $i -lt $Values.Length; $i++) {
            $value = $Values[$i]
            if ($null -eq $value) { continue }
            $result = if ($value -is [int] -or $value -is [long]) {
                [InstallerDatabase]::MsiRecordSetInteger($record, $i + 1, $value)
            } else {
                [InstallerDatabase]::MsiRecordSetString($record, $i + 1, $value)
            }
            Assert-Msi $result "Set field $($i + 1) in $Table"
        }
        Invoke-Sql ('INSERT INTO `{0}` ({1}) VALUES ({2})' -f $Table, $columns[$Table], (('?' * $Values.Length).ToCharArray() -join ', ')) $record
    } finally {
        $null = [InstallerDatabase]::MsiCloseHandle($record)
    }
}

[uint32]$database = [uint32]$summary = [uint32]$stream = 0
try {
    $cabinet = Join-Path $work 'app.cab'
    @"
.Set CabinetNameTemplate=app.cab
.Set DiskDirectoryTemplate="$work"
.Set Cabinet=on
.Set Compress=on
.Set CompressionType=MSZIP
.Set MaxDiskSize=0
.Set MaxCabinetSize=0
.Set InfFileName="$work\cab.inf"
.Set RptFileName="$work\cab.rpt"
"$Application" AppExe
"@ | Set-Content -LiteralPath (Join-Path $work 'app.ddf') -Encoding ascii
    & makecab.exe /F (Join-Path $work 'app.ddf') | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $cabinet)) { throw 'Could not build the installer cabinet.' }

    $tempMsi = Join-Path $work 'DeskBridge.msi'
    Assert-Msi ([InstallerDatabase]::MsiOpenDatabase($tempMsi, [IntPtr]3, [ref]$database)) "Create MSI database"

    $tables = @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)'
        'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)'
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)'
        'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)'
        'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)'
        'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)'
        'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)'
        'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(255) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72), `DisplayResourceDLL` CHAR(255), `DisplayResourceId` SHORT, `DescriptionResourceDLL` CHAR(255), `DescriptionResourceId` SHORT PRIMARY KEY `Shortcut`)'
        'CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)'
        'CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)'
        'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255) LOCALIZABLE, `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)'
        'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)'
        'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    )
    $columns = @{}
    foreach ($table in $tables) {
        Invoke-Sql $table
        $schema = $table -split ' PRIMARY KEY ', 2
        $identifiers = [regex]::Matches($schema[0], '`[^`]+`').Value
        $columns[$identifiers[0].Trim('`')] = ($identifiers | Select-Object -Skip 1) -join ', '
    }

    $properties = [ordered]@{
        ProductCode=$productCode; ProductName='DeskBridge'; ProductVersion=$productVersion
        ProductLanguage='1033'; Manufacturer='DeskBridge'; UpgradeCode=$upgradeCode
        ALLUSERS='2'; MSIINSTALLPERUSER='1'; ARPNOMODIFY='1'; LIMITUI='1'
        ARPCOMMENTS='Share a keyboard and mouse between Windows devices.'
        ARPURLINFOABOUT='https://github.com/calinadrian/DeskBridge'
        SecureCustomProperties='REMOVE_OLDER;NEWER_INSTALLED'
    }
    foreach ($entry in $properties.GetEnumerator()) { Add-Row Property @($entry.Key, $entry.Value) }

    Add-Row Directory @('TARGETDIR', $null, 'SourceDir')
    Add-Row Directory @('LocalAppDataFolder', 'TARGETDIR', '.')
    Add-Row Directory @('UserPrograms', 'LocalAppDataFolder', 'Programs')
    Add-Row Directory @('INSTALLDIR', 'UserPrograms', 'DeskBridge')
    Add-Row Directory @('ProgramMenuFolder', 'TARGETDIR', '.')
    Add-Row Component @('Executable', '{C2275B6D-5C58-4B2B-BCC8-27C5C61B93D0}', 'INSTALLDIR', 256, $null, 'AppExe')
    Add-Row Component @('StartMenu', '{8F82CA9F-7E64-4DAC-8BBB-1F78B5221BD8}', 'ProgramMenuFolder', 4, $null, 'ShortcutMarker')
    Add-Row Feature @('DeskBridge', $null, 'DeskBridge', 'DeskBridge for Windows', 1, 1, 'INSTALLDIR', 0)
    Add-Row FeatureComponents @('DeskBridge', 'Executable')
    Add-Row FeatureComponents @('DeskBridge', 'StartMenu')
    Add-Row File @('AppExe', 'Executable', 'DeskBridge.exe', (Get-Item -LiteralPath $Application).Length, $fullVersion, $null, 16384, 1)
    Add-Row Media @(1, 1, $null, '#app.cab', $null, $null)
    Add-Row Registry @('ShortcutMarker', 1, 'Software\DeskBridge\Installer', 'StartMenu', '#1', 'StartMenu')
    Add-Row Shortcut @('AppShortcut', 'ProgramMenuFolder', 'DeskBridge', 'StartMenu', '[#AppExe]', $null, 'Open DeskBridge', $null, $null, $null, 1, 'INSTALLDIR', $null, $null, $null, $null)
    Add-Row RemoveFile @('PreviousExecutable', 'Executable', 'DeskBridge.exe.previous', 'INSTALLDIR', 2)
    Add-Row Upgrade @($upgradeCode, $null, $productVersion, $null, 0, $null, 'REMOVE_OLDER')
    Add-Row Upgrade @($upgradeCode, $productVersion, $null, $null, 2, $null, 'NEWER_INSTALLED')
    Add-Row LaunchCondition @('Installed OR NOT NEWER_INSTALLED', 'A newer DeskBridge installer is already installed.')
    Add-Row LaunchCondition @('Installed OR WindowsBuild >= 10240', 'DeskBridge requires Windows 10 or Windows 11.')

    $actions = [ordered]@{
        FindRelatedProducts=25; LaunchConditions=100; CostInitialize=800; FileCost=900
        CostFinalize=1000; InstallValidate=1400; InstallInitialize=1500; RemoveExistingProducts=1510
        ProcessComponents=1600; UnpublishFeatures=1800; RemoveRegistryValues=2600
        RemoveShortcuts=3200; RemoveFiles=3500; RemoveFolders=3600; InstallFiles=4000
        CreateShortcuts=4500; WriteRegistryValues=5000; RegisterUser=6000; RegisterProduct=6100
        PublishFeatures=6300; PublishProduct=6400; InstallFinalize=6600
    }
    foreach ($action in $actions.GetEnumerator()) {
        $condition = if ($action.Key -eq 'RemoveExistingProducts') { 'REMOVE_OLDER' } else { $null }
        Add-Row InstallExecuteSequence @($action.Key, $condition, $action.Value)
    }

    $stream = [InstallerDatabase]::MsiCreateRecord(2)
    Assert-Msi ([InstallerDatabase]::MsiRecordSetString($stream, 1, 'app.cab')) "Name embedded cabinet"
    Assert-Msi ([InstallerDatabase]::MsiRecordSetStream($stream, 2, $cabinet)) "Embed cabinet"
    Invoke-Sql 'INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)' $stream

    Assert-Msi ([InstallerDatabase]::MsiGetSummaryInformation($database, $null, 9, [ref]$summary)) "Open MSI summary"
    $fields = @{ 2='Installation Database'; 3="DeskBridge $productVersion"; 4='DeskBridge'
        7='x64;1033'; 9=$packageCode; 14=500; 15=10; 19=2 }
    foreach ($field in $fields.GetEnumerator()) {
        $result = if ($field.Value -is [int]) {
            [InstallerDatabase]::MsiSummaryInfoSetProperty($summary, $field.Key, 3, $field.Value, [IntPtr]::Zero, $null)
        } else {
            [InstallerDatabase]::MsiSummaryInfoSetProperty($summary, $field.Key, 30, 0, [IntPtr]::Zero, $field.Value)
        }
        Assert-Msi $result "Set MSI summary field $($field.Key)"
    }
    Assert-Msi ([InstallerDatabase]::MsiSummaryInfoPersist($summary)) "Save MSI summary"
    Assert-Msi ([InstallerDatabase]::MsiDatabaseCommit($database)) "Save MSI database"
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Output)) | Out-Null
    Copy-Item -LiteralPath $tempMsi -Destination $Output -Force
    Write-Host "Built $Output"
} finally {
    foreach ($handle in $stream, $summary, $database) {
        if ($handle) { $null = [InstallerDatabase]::MsiCloseHandle($handle) }
    }
    Get-ChildItem -LiteralPath $work -File | Remove-Item -Force
    Remove-Item -LiteralPath $work
}
