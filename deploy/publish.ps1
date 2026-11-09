<#
.SYNOPSIS
  Builds every CBH-NMS component into .\publish (run on a build PC with .NET 10 SDK and Node 20+).

  publish\Core    — HO web app + API (IIS site or Windows Service), UI included in wwwroot
  publish\Poller  — HO Central Poller (Windows Service)
  publish\Agent   — Branch Agent, self-contained win-x64 (branch PCs need no .NET install)
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\publish'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location (Join-Path $root 'src\Cbh.Nms.Web')
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    npm run build
    if ($LASTEXITCODE) { throw 'UI build failed' }
} finally { Pop-Location }

dotnet test (Join-Path $root 'CbhNms.slnx') -c Release
if ($LASTEXITCODE) { throw 'Tests failed — not publishing' }

dotnet publish (Join-Path $root 'src\Cbh.Nms.Core') -c Release -o (Join-Path $Out 'Core')
dotnet publish (Join-Path $root 'src\Cbh.Nms.Poller') -c Release -o (Join-Path $Out 'Poller')
dotnet publish (Join-Path $root 'src\Cbh.Nms.Agent') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o (Join-Path $Out 'Agent')
Copy-Item (Join-Path $PSScriptRoot 'install-agent.ps1') (Join-Path $Out 'Agent') -Force
Write-Host "Published to $Out" -ForegroundColor Green
