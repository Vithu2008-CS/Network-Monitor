param([switch]$NoAdmin, [switch]$NoBrowser, [switch]$Share)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location -LiteralPath $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$url = 'http://localhost:5099'
try {
    $health = Invoke-RestMethod "$url/api/health" -TimeoutSec 2
    if ($health.app -eq 'Flow') {
        $state = Invoke-RestMethod "$url/api/state" -TimeoutSec 3
        if (($NoAdmin -or $state.isAdministrator) -and ([bool]$state.access.mobileEnabled -eq [bool]$Share)) {
            if (-not $NoBrowser) { Start-Process $url }
            Write-Host "Flow is already running at $url"
            exit 0
        }
        Invoke-RestMethod -Method Post -Uri "$url/api/stop" -Headers @{'X-Flow-Request'='1'} | Out-Null
        Start-Sleep -Seconds 2
    }
} catch { }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $NoAdmin -and -not $isAdmin) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($NoBrowser) { $arguments += ' -NoBrowser' }
    if ($Share) { $arguments += ' -Share' }
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -WorkingDirectory $root }
    catch { Write-Host 'Elevation cancelled. Run Start Monitor.cmd -NoAdmin for adapter totals without app capture.'; exit 1 }
    exit 0
}
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    Write-Host 'Installing a project-local .NET SDK for Flow...'
    $tools = Join-Path $root '.tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $installer = Join-Path $tools 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing -OutFile $installer
    & $installer -Channel 10.0 -Architecture x64 -InstallDir (Join-Path $tools 'dotnet') -NoPath
    if (-not (Test-Path -LiteralPath $dotnet)) { throw '.NET installation failed.' }
}
$target = Join-Path $root 'publish/Flow.dll'
$sources = Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.cs','.csproj' }
$needsBuild = -not (Test-Path -LiteralPath $target)
if (-not $needsBuild) { $needsBuild = @($sources | Where-Object { $_.LastWriteTime -gt (Get-Item -LiteralPath $target).LastWriteTime }).Count -gt 0 }
if ($needsBuild) {
    & $dotnet publish (Join-Path $root 'Flow.csproj') -c Release -o (Join-Path $root 'publish') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. See the messages above.' }
}
$ruleName = 'Flow-Mobile-LocalSubnet'
if ($Share) {
    if (-not $isAdmin) { throw 'Mobile sharing requires administrator access for its scoped Windows firewall rule.' }
    $config = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.HardwareInterface } | Sort-Object @{Expression={ $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' };Descending=$true} | Select-Object -First 1
    if (-not $config) { throw 'No active Wi-Fi or Ethernet IPv4 connection was found.' }
    $shareAddress = $config.IPv4Address.IPAddress | Select-Object -First 1
    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $ruleName -DisplayName 'Flow phone view (local subnet)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5099 -LocalAddress $shareAddress -RemoteAddress LocalSubnet -InterfaceAlias $config.InterfaceAlias -Program $dotnet -Profile Any | Out-Null
    Write-Host "Phone view enabled on $shareAddress. Open Settings on the laptop for its private pairing link."
} elseif ($isAdmin) {
    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
Write-Host "Flow: $url"
Write-Host 'Keep this window open. Press Ctrl+C to stop monitoring.'
if (-not $NoBrowser) {
    $browserJob = Start-Job -ScriptBlock {
        param($address)
        for ($i = 0; $i -lt 30; $i++) {
            try {
                $health = Invoke-RestMethod "$address/api/health" -TimeoutSec 1
                if ($health.app -eq 'Flow') { Start-Process $address; return }
            } catch { }
            Start-Sleep -Seconds 1
        }
    } -ArgumentList $url
}
$serverArguments = @($target, '--contentRoot', $root)
if ($Share) { $serverArguments += @('--share', '--share-address', $shareAddress) }
try { & $dotnet @serverArguments }
finally {
    if ($browserJob) { Stop-Job $browserJob -ErrorAction SilentlyContinue; Remove-Job $browserJob -Force -ErrorAction SilentlyContinue }
}
