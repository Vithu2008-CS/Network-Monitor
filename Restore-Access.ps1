$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -WorkingDirectory $PSScriptRoot -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"')
    exit
}
try {
    Invoke-RestMethod -Method Post -Uri 'http://localhost:5099/api/controls/restore' -Headers @{'X-Flow-Request'='1'} -TimeoutSec 30 | Out-Null
    Write-Host 'All Flow app controls disabled. Other firewall rules were untouched.'
} catch {
    # Stop the monitor before cleanup so a surviving quota loop cannot recreate a rule.
    try { Invoke-RestMethod -Method Post -Uri 'http://localhost:5099/api/stop' -Headers @{'X-Flow-Request'='1'} -TimeoutSec 3 | Out-Null; Start-Sleep -Seconds 3 } catch { }
    & (Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe') (Join-Path $PSScriptRoot 'publish/Flow.dll') --restore-controls
    if ($LASTEXITCODE -ne 0) { throw 'Recovery failed. Check the error above before restarting the monitor.' }
}
