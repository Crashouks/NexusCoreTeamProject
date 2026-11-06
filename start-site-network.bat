@echo off
cd /d "%~dp0"
title NexusCore - Website + API (Network Mode)
echo.
echo  Starting NexusCore for REMOTE devices (other networks / laptops)
echo  API binds 0.0.0.0:5000  ^|  Website 0.0.0.0:5173
echo  Set PUBLIC_API_URL in nexuscore\.env for your Tailscale URLs
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-network.ps1"
pause
