@echo off
powershell.exe -NoProfile -Command "try { Invoke-RestMethod -Method Post -Uri 'http://localhost:5099/api/stop' -Headers @{'X-Flow-Request'='1'} | Out-Null; Write-Host 'Flow stopped.' } catch { Write-Host 'Flow is not running or could not be stopped.' }"
