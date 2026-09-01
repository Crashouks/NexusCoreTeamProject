# NexusCore - one-command dev launcher (API + React client in one terminal)
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
    } catch {
        return $false
    }
}

function Ensure-MySql {
    if (Test-Port 3306) {
        Write-Host '(mysql) Already running on port 3306' -ForegroundColor DarkGray
        return
    }

    Write-Host '(mysql) Port 3306 not reachable - trying to start MySQL service...' -ForegroundColor Yellow
    foreach ($name in @("MySQL80", "MySQL", "MariaDB", "wampmysqld64", "wampmysqld")) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        if ($svc.Status -ne "Running") {
            try {
                Start-Service $name
                Start-Sleep -Seconds 3
                Write-Host "(mysql) Started service: $name" -ForegroundColor Green
                return
            } catch {
                Write-Host "(mysql) Could not start $name (admin rights may be required)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "(mysql) Service $name is running but port 3306 is closed - check your DB config" -ForegroundColor Yellow
            return
        }
    }
    Write-Host '(mysql) No MySQL service found. Start MySQL/XAMPP manually if the API fails to connect.' -ForegroundColor Yellow
}

Require-Command node "Install Node.js 18+ from https://nodejs.org/"
Require-Command npm "Install Node.js (includes npm) from https://nodejs.org/"
Require-Command dotnet "Install .NET 10 SDK from https://dotnet.microsoft.com/download"

Write-Host ""
Write-Host "  NexusCore dev server" -ForegroundColor Cyan
Write-Host "  App:  http://localhost:5173  (opens when ready)" -ForegroundColor DarkGray
Write-Host "  API:  http://localhost:5000" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Tip: In Visual Studio, press F5 on NexusCore.Api - it also starts the client." -ForegroundColor DarkGray
Write-Host ""

& node setup.js

Write-Host '(ports) Freeing 5000/5173 before start...' -ForegroundColor DarkGray
& "$PSScriptRoot\stop.ps1" -Quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host '(ports) Could not free ports 5000/5173. Run stop.bat, close Visual Studio F5, then try again.' -ForegroundColor Red
    exit 1
}

Ensure-MySql

if (-not (Test-Path ".\node_modules\concurrently")) {
    Write-Host '(setup) Installing launcher dependencies...' -ForegroundColor Yellow
    npm install --no-fund --no-audit
}

if (-not (Test-Path ".\client\node_modules")) {
    Write-Host '(setup) Installing client dependencies...' -ForegroundColor Yellow
    npm run install:all
}

if (-not (Test-Path ".\.db-seeded")) {
    Write-Host '(setup) First run — seeding database (requires MySQL on port 3306)...' -ForegroundColor Yellow
    npm run seed
    if ($LASTEXITCODE -ne 0) {
        Write-Host '(setup) Database seed failed. Start MySQL, set DB_PASSWORD in .env, then run: npm run seed' -ForegroundColor Red
        exit 1
    }
    New-Item -Path ".\.db-seeded" -ItemType File -Force | Out-Null
    Write-Host '(setup) Database ready.' -ForegroundColor Green
}

# Open browser in background — must NOT run inside concurrently -k or it kills the servers when done
Start-Job {
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline) {
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new()
            $tcp.Connect("127.0.0.1", 5173)
            $tcp.Close()
            Start-Process "http://localhost:5173"
            break
        } catch {
            Start-Sleep -Seconds 2
        }
    }
} | Out-Null

npm run dev
