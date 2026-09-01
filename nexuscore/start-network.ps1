# NexusCore - network mode (reachable from other devices / networks via PUBLIC_* URLs)
Set-Location $PSScriptRoot

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Require-Command([string]$Name, [string]$InstallHint) {
    if (Test-Command $Name) { return }
    Write-Host "Missing required tool: $Name" -ForegroundColor Red
    Write-Host $InstallHint -ForegroundColor Yellow
    exit 1
}

function Test-Port([int]$Port) {
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $tcp.Connect("127.0.0.1", $Port)
        $tcp.Close()
        return $true
    } catch { return $false }
}

function Ensure-MySql {
    if (Test-Port 3306) { return }
    Write-Host '(mysql) Port 3306 not reachable - start MySQL/XAMPP' -ForegroundColor Yellow
}

Require-Command node "Install Node.js 18+ from https://nodejs.org/"
Require-Command npm "Install Node.js from https://nodejs.org/"
Require-Command dotnet "Install .NET 10 SDK from https://dotnet.microsoft.com/download"

Write-Host ""
if ($env:HTTPS_MODE -eq '1') {
    Write-Host "  NexusCore - NETWORK + HTTPS MODE" -ForegroundColor Cyan
} else {
    Write-Host "  NexusCore - NETWORK MODE" -ForegroundColor Cyan
}
Write-Host "  Site/API listen on all interfaces (0.0.0.0)" -ForegroundColor DarkGray
Write-Host ""

$env:NETWORK_MODE = "1"
$env:API_BIND = "0.0.0.0"

# Load .env early so HTTPS_MODE is available for sync
if (Test-Path ".\.env") {
    Get-Content ".\.env" | ForEach-Object {
        if ($_ -match '^\s*([^#=]+)=(.*)$') {
            $k = $matches[1].Trim()
            $v = $matches[2].Trim()
            if ($k -in @('PUBLIC_API_URL', 'PUBLIC_WEB_URL', 'PORT', 'NETWORK_MODE', 'API_BIND', 'HTTPS_MODE')) {
                Set-Item -Path "env:$k" -Value $v
            }
        }
    }
}
$env:NETWORK_MODE = "1"
$env:API_BIND = "0.0.0.0"

& node setup.js

Write-Host '(ports) Freeing 5000/5173 before start...' -ForegroundColor DarkGray
& "$PSScriptRoot\stop.ps1" -Quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host '(ports) Could not free ports 5000/5173. Run stop.bat, close Visual Studio F5, then try again.' -ForegroundColor Red
    exit 1
}

Ensure-MySql

if (-not (Test-Path ".\node_modules\concurrently")) {
    npm install --no-fund --no-audit
}

if (-not (Test-Path ".\client\node_modules")) {
    npm run install:all
}

if (-not (Test-Path ".\.db-seeded")) {
    npm run seed
    if ($LASTEXITCODE -ne 0) { exit 1 }
    New-Item -Path ".\.db-seeded" -ItemType File -Force | Out-Null
}

$syncArgs = @('--network')
if ($env:HTTPS_MODE -eq '1') { $syncArgs += '--https' }
& node scripts/sync-network-env.js @syncArgs

if (-not (Test-Path ".\.env")) {
    Copy-Item ".env.example" ".env"
}

# Reload .env for dotnet process
Get-Content ".\.env" | ForEach-Object {
    if ($_ -match '^\s*([^#=]+)=(.*)$') {
        $k = $matches[1].Trim()
        $v = $matches[2].Trim()
        if ($k -in @('PUBLIC_API_URL', 'PUBLIC_WEB_URL', 'PORT', 'NETWORK_MODE', 'API_BIND', 'HTTPS_MODE')) {
            Set-Item -Path "env:$k" -Value $v
        }
    }
}
$env:NETWORK_MODE = "1"
$env:API_BIND = "0.0.0.0"

if ($env:HTTPS_MODE -eq '1' -and $env:PUBLIC_WEB_URL) {
    Write-Host "  HTTPS player URL: $($env:PUBLIC_WEB_URL)" -ForegroundColor Green
    Write-Host ""
}

npm run dev:network
