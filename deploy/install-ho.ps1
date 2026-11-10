<#
.SYNOPSIS
  Installs the Head Office components (Core Platform + Central Poller) as Windows Services on the
  HO app server. Run elevated from the repository's deploy folder after .\publish.ps1.
  For IIS hosting instead of a service, see docs\DEPLOYMENT.md.

.EXAMPLE
  .\install-ho.ps1 -ConnectionString "Host=db01;Database=cbhnms;Username=cbhnms;Password=...;SSL Mode=Require" `
                   -SuperAdmin "PDCL\maruf.ahmed" -Urls "https://+:443"
#>
param(
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][string]$SuperAdmin,
    [string]$Urls = 'http://+:5219',
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\publish'),
    [string]$InstallRoot = "$env:ProgramFiles\CBH-NMS"
)
$ErrorActionPreference = 'Stop'

function Install-Svc($name, $folder, $exeName, $description, [hashtable]$settings) {
    $dir = Join-Path $InstallRoot $folder
    if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    # An upgrade keeps the server's own settings; a first install writes them.
    $prod = Join-Path $dir 'appsettings.Production.json'
    $firstInstall = -not (Test-Path $prod)
    Copy-Item (Join-Path $PublishDir "$folder\*") $dir -Recurse -Force -Exclude 'appsettings.Production.json'
    if ($firstInstall) { $settings | ConvertTo-Json -Depth 6 | Set-Content $prod -Encoding utf8 }
    if (-not (Get-Service $name -ErrorAction SilentlyContinue)) {
        New-Service -Name $name -BinaryPathName "`"$(Join-Path $dir $exeName)`"" -DisplayName $name `
            -StartupType Automatic -Description $description | Out-Null
    }
    sc.exe failure $name reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}

New-Item -ItemType Directory -Force "$env:ProgramData\CBH-NMS\keys" | Out-Null
icacls "$env:ProgramData\CBH-NMS\keys" /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

Install-Svc 'CBH-NMS Core' 'Core' 'Cbh.Nms.Core.exe' 'PDCL CBH-NMS Core Platform (API, UI, evaluation engine).' @{
    ConnectionStrings = @{ Nms = $ConnectionString }
    Bootstrap = @{ SuperAdminUser = $SuperAdmin }
    Kestrel = @{ Endpoints = @{ Main = @{ Url = $Urls } } }
    Agent = @{ Auth = @{ AllowedNetworks = @('10.0.0.0/8', '172.16.0.0/12') } }   # MPLS + VPN source ranges
}
Install-Svc 'CBH-NMS Poller' 'Poller' 'Cbh.Nms.Poller.exe' 'PDCL CBH-NMS Central Poller (branch reachability over MPLS and VPN).' @{
    ConnectionStrings = @{ Nms = $ConnectionString }
}
[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
[Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Machine')
Start-Service 'CBH-NMS Core'
Start-Service 'CBH-NMS Poller'
Write-Host "Installed. Edit $InstallRoot\Core\appsettings.Production.json for SMTP, SMS, Task Management and AD groups, then restart 'CBH-NMS Core'." -ForegroundColor Green
