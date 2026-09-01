param(
    [switch]$Quiet,
    [int]$WaitSeconds = 10
)

# Stop NexusCore dev processes on API + Vite ports (including Vite fallback 5174+)
$ports = 5000, 5173, 5174, 5175, 5176
$stoppedIds = [System.Collections.Generic.HashSet[int]]::new()

function Write-StopLog {
    param([string]$Message, [string]$Color = "Gray")
    if ($Quiet) { return }
    Write-Host $Message -ForegroundColor $Color
}

function Get-PortsInUse {
    $busy = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($port in $ports) {
        $listen = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $listen) { [void]$busy.Add($port) }
    }
    return @($busy)
}

function Stop-ProcessTree {
    param([int]$ProcessId, [string]$Reason)
    if ($ProcessId -le 0 -or $stoppedIds.Contains($ProcessId)) { return }

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return }

    Write-StopLog "Stopping $($proc.ProcessName) (PID $ProcessId) - $Reason" "Yellow"
    cmd /c "taskkill /PID $ProcessId /T /F >nul 2>&1"
    [void]$stoppedIds.Add($ProcessId)
}

foreach ($port in $ports) {
    $conns = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    foreach ($conn in $conns) {
        Stop-ProcessTree $conn.OwningProcess "port $port"
    }
}

Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'NexusCore\.Api' } |
    ForEach-Object { Stop-ProcessTree $_.ProcessId "dotnet NexusCore.Api" }

Get-Process -Name "NexusCore.Api" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-ProcessTree $_.Id "NexusCore.Api" }

Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'vite|concurrently|wait-on|NexusCore\\nexuscore' } |
    ForEach-Object { Stop-ProcessTree $_.ProcessId "node dev server" }

Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'concurrently|npm run dev' } |
    ForEach-Object { Stop-ProcessTree $_.ProcessId "npm dev launcher" }

$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    $still = Get-PortsInUse
    if ($still.Count -eq 0) { break }
    Start-Sleep -Milliseconds 400
} while ((Get-Date) -lt $deadline)

$ts = Get-Command tailscale -ErrorAction SilentlyContinue
if (-not $ts) {
    $default = "${env:ProgramFiles}\Tailscale\tailscale.exe"
    if (Test-Path $default) { $ts = Get-Command $default -ErrorAction SilentlyContinue }
}
if ($ts) {
    & $ts.Source serve reset 2>$null
    Write-StopLog "Tailscale HTTPS serve disabled (back to HTTP-only)." "DarkGray"
}

if ($stoppedIds.Count -eq 0 -and $still.Count -eq 0) {
    Write-StopLog "Nothing running on ports 5000 or 5173-5176." "DarkGray"
    exit 0
}

if ($still.Count -gt 0) {
    Write-StopLog "Stopped $($stoppedIds.Count) process(es), but port(s) still busy: $($still -join ', ')" "Red"
    Write-StopLog "Close Visual Studio (F5), other terminals, then run stop.bat again." "DarkGray"
    Write-StopLog "Diagnose: netstat -ano | findstr :5000" "DarkGray"
    exit 1
}

Write-StopLog "Stopped $($stoppedIds.Count) process(es). Ports 5000 and 5173 are free." "Green"
exit 0
