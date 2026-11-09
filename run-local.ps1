<#
.SYNOPSIS
  Runs the whole CBH-NMS stack on this PC for development / demo:
  PostgreSQL 17 + TimescaleDB in Docker, Core on http://localhost:5219, Central Poller, and a Branch Agent.

  .\run-local.ps1                 start everything (each component in its own window)
  .\run-local.ps1 -Stop           stop the .NET processes (the database container keeps running)
  .\run-local.ps1 -EnrollToken X  first agent run: register it with a token from the UI
#>
param([switch]$Stop, [string]$EnrollToken, [switch]$SkipUiBuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Stop) {
    Get-Process Cbh.Nms.Core, Cbh.Nms.Poller, Cbh.Nms.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host 'Stopped Core, Poller and Agent.'
    return
}

# 1. Database (Docker is used only on dev PCs; production uses PostgreSQL for Windows, SRS §2.3).
$db = docker ps -a --filter name=^cbhnms-db$ --format '{{.Status}}'
if (-not $db) {
    docker run -d --name cbhnms-db --restart unless-stopped -p 5432:5432 -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=cbhnms `
        -v cbhnms-pgdata:/var/lib/postgresql/data timescale/timescaledb:latest-pg17 | Out-Null
} elseif ($db -notlike 'Up*') { docker start cbhnms-db | Out-Null }
do { Start-Sleep 1 } until ((docker exec cbhnms-db pg_isready -U postgres) -match 'accepting')

# 2. UI + build
if (-not $SkipUiBuild) {
    Push-Location "$root\src\Cbh.Nms.Web"
    if (-not (Test-Path node_modules)) { npm install --no-audit --no-fund }
    npm run build
    Pop-Location
}
dotnet build "$root\CbhNms.slnx" -v q
if ($LASTEXITCODE) { throw 'Build failed' }

# 3. Services, each in its own window so logs are visible.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$core = "`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='http://localhost:5219'; `$env:Bootstrap__SuperAdminUser='$me'; dotnet run --no-build --no-launch-profile --project '$root\src\Cbh.Nms.Core'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $core
do { Start-Sleep 2 } until ((Test-NetConnection localhost -Port 5219 -WarningAction SilentlyContinue).TcpTestSucceeded)

$poller = "`$env:ConnectionStrings__Nms='Host=localhost;Database=cbhnms;Username=postgres;Password=postgres'; dotnet run --no-build --project '$root\src\Cbh.Nms.Poller'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $poller

$agentData = "$env:ProgramData\CBH-NMS\dev-agent"
$agent = "`$env:Agent__HoBaseUrl='http://localhost:5219/'; `$env:Agent__DataDirectory='$agentData'; `$env:Agent__EnrollToken='$EnrollToken'; dotnet run --no-build --project '$root\src\Cbh.Nms.Agent'"
if ($EnrollToken -or (Test-Path "$agentData\agent.key")) {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', $agent
} else {
    Write-Host 'Agent not started: create a token in the UI (Branches & Devices -> Agents), then run .\run-local.ps1 -SkipUiBuild -EnrollToken <token>' -ForegroundColor Yellow
}
Start-Process 'http://localhost:5219/'
Write-Host 'CBH-NMS is running at http://localhost:5219/' -ForegroundColor Green
