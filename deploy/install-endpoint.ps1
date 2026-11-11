<#
.SYNOPSIS
  Installs (or removes) the CBH-NMS Endpoint Agent on a user PC. Run elevated from the published Endpoint
  folder. Suitable for GPO / SCCM / Intune: one enrollment key per branch, no prompts.

  Installs:
    - Windows Service "CBH-NMS Endpoint Agent" (health, software inventory, network; runs as LocalSystem)
    - Scheduled task  "CBH-NMS Session Helper" (starts at every user logon, no window; reports which program is in
      the foreground only while HO has activity tracking switched on — see docs\DECISIONS.md D-14)

.EXAMPLE
  .\install-endpoint.ps1 -HoBaseUrl "https://nms.pdcl.local/" -EnrollKey "AbC...xyz"
  .\install-endpoint.ps1 -Uninstall
#>
param(
    [string]$HoBaseUrl,
    [string]$EnrollKey,
    [string]$InstallDir = "$env:ProgramFiles\CBH-NMS\Endpoint",
    [string]$DataDir = "$env:ProgramData\CBH-NMS\Endpoint",
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}
$svc = 'CBH-NMS Endpoint Agent'
$task = 'CBH-NMS Session Helper'

function Stop-All {
    if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force }
    Get-Process Cbh.Nms.Endpoint.Session -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Uninstall) {
    Stop-All
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $task -Confirm:$false }
    if (Get-Service $svc -ErrorAction SilentlyContinue) { sc.exe delete $svc | Out-Null }
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Write-Host "Removed. Local data left in $DataDir (delete it to forget this PC's identity)." -ForegroundColor Green
    return
}
if (-not $HoBaseUrl) { throw '-HoBaseUrl is required' }

Stop-All
New-Item -ItemType Directory -Force $InstallDir, $DataDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot '*') $InstallDir -Recurse -Force -Exclude 'install-endpoint.ps1', 'appsettings.Production.json'

# The key is only needed until the first registration; afterwards the PC's own key lives in endpoint.key (DPAPI).
@{ Endpoint = @{ HoBaseUrl = $HoBaseUrl; EnrollKey = $EnrollKey; DataDirectory = $DataDir } } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $InstallDir 'appsettings.Production.json') -Encoding utf8

# Only SYSTEM and Administrators may read the data folder (PC key, unsent data).
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    New-Service -Name $svc -BinaryPathName "`"$(Join-Path $InstallDir 'Cbh.Nms.Endpoint.exe')`"" -DisplayName $svc -StartupType Automatic `
        -Description 'PDCL CBH-NMS: reports this PC''s health, installed software and network to IT. See your IT monitoring policy.' | Out-Null
}
sc.exe failure $svc reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

# Session helper: every user, at logon, without a window, as that user (never elevated).
$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'Cbh.Nms.Endpoint.Session.exe')
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited     # BUILTIN\Users
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances Parallel -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'CBH-NMS: reports program usage to IT when enabled by PDCL policy. No keystrokes, screenshots, titles or URLs.' -Force | Out-Null

Start-Service $svc
Start-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue   # users already logged on; others start at next logon
Write-Host "CBH-NMS Endpoint Agent installed. It appears under User PCs within a minute. Logs: Windows Event Log (Application)." -ForegroundColor Green
