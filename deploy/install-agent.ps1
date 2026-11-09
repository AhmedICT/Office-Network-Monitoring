<#
.SYNOPSIS
  Installs the CBH-NMS Branch Agent as a Windows Service on a branch PC (run elevated, from the
  published Agent folder). The enrollment token comes from Branches & Devices → Agents in the web UI.

.EXAMPLE
  .\install-agent.ps1 -HoBaseUrl "https://nms.pdcl.local/" -EnrollToken "AbC...xyz"
#>
param(
    [Parameter(Mandatory)][string]$HoBaseUrl,
    [string]$EnrollToken,
    [string]$InstallDir = "$env:ProgramFiles\CBH-NMS\Agent",
    [string]$DataDir = "$env:ProgramData\CBH-NMS\Agent"
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}
$svc = 'CBH-NMS Agent'
$exe = Join-Path $InstallDir 'Cbh.Nms.Agent.exe'

if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Write-Host 'Stopping existing service for upgrade...'
    Stop-Service $svc -Force
}
New-Item -ItemType Directory -Force $InstallDir, $DataDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot '*') $InstallDir -Recurse -Force -Exclude 'install-agent.ps1'

# The token is only needed until the first successful registration; the key then lives in agent.key (DPAPI).
$settings = @{
    Agent = @{ HoBaseUrl = $HoBaseUrl; EnrollToken = $EnrollToken; DataDirectory = $DataDir }
} | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $InstallDir 'appsettings.Production.json') -Value $settings -Encoding utf8

# Only SYSTEM and Administrators may read the data folder (agent key, cached config with SNMP secrets).
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName $svc -StartupType Automatic `
        -Description 'PDCL CBH-NMS Branch Agent: polls local devices and reports to Head Office.' | Out-Null
}
# NFR-07: restart automatically on failure (after 30 s, 60 s, then every 2 min).
sc.exe failure $svc reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
[Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Machine')
Start-Service $svc
Write-Host "CBH-NMS Agent installed and started. Logs: Windows Event Log (Application) and $DataDir" -ForegroundColor Green
