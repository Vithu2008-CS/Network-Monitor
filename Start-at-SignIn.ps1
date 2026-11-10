param([ValidateSet('Status','Enable','Disable')][string]$Action = 'Status', [switch]$Share)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskName = 'Flow Network Monitor'
$root = $PSScriptRoot
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $identity.User.Value
$task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
$taskSid = ''
if ($task) {
    try {
        $taskSid = if ($task.Principal.UserId -like 'S-1-*') { $task.Principal.UserId } else { ([Security.Principal.NTAccount]$task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value }
    } catch { }
}
$owned = -not $task -or ($task.Description -like 'Flow network monitor | *' -and $taskSid -eq $sid)
if ($Action -ne 'Status') {
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Start Flow as administrator to configure sign-in startup.' }
    if (-not $owned) { throw 'A task with this name belongs to another account or application; it was left unchanged.' }
    if ($Action -eq 'Disable') {
        if ($task) { Disable-ScheduledTask -TaskName $taskName -TaskPath '\' | Out-Null }
    } else {
        $launch = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $root 'Start-Monitor.ps1') + '" -NoBrowser'
        if ($Share) { $launch += ' -Share' }
        $executable = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $command = New-ScheduledTaskAction -Execute $executable -Argument $launch -WorkingDirectory $root
        $logon = New-ScheduledTaskTrigger -AtLogOn -User $sid
        $triggerClass = Get-CimClass -Namespace 'Root/Microsoft/Windows/TaskScheduler' -ClassName 'MSFT_TaskSessionStateChangeTrigger'
        $unlock = New-CimInstance -ClientOnly -CimClass $triggerClass -Property @{ StateChange = [uint32]8; UserId = $sid; Enabled = $true }
        $principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        $definition = New-ScheduledTask -Action $command -Trigger @($logon,$unlock) -Principal $principal -Settings $settings -Description ('Flow network monitor | ' + $root)
        Register-ScheduledTask -TaskName $taskName -TaskPath '\' -InputObject $definition -Force | Out-Null
    }
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
}
$info = if ($task) { Get-ScheduledTaskInfo -TaskName $taskName -TaskPath '\' } else { $null }
[pscustomobject]@{
    installed = [bool]$task
    enabled = [bool]($task -and $task.State -ne 'Disabled')
    owned = [bool]$owned
    taskName = $taskName
    triggers = if ($task) { @($task.Triggers | ForEach-Object { if ($_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger') { 'Sign-in' } elseif ($_.CimClass.CimClassName -eq 'MSFT_TaskSessionStateChangeTrigger' -and $_.StateChange -eq 8) { 'Session unlock' } }) } else { @() }
    shareMode = [bool]($task -and $task.Actions.Arguments -match ' -Share(?:\s|$)')
    lastRunTime = if ($info -and $info.LastRunTime.Year -gt 2000) { $info.LastRunTime.ToString('o') } else { $null }
    lastResult = if ($info) { $info.LastTaskResult } else { $null }
    message = if (-not $owned) { 'A task with this name belongs to another account or application and was not changed.' } elseif ($task -and $task.State -ne 'Disabled') { 'Starts in the background at Windows sign-in and unlock, with administrator capture. No password is stored.' } else { 'Automatic startup is off. The current running monitor is unaffected.' }
} | ConvertTo-Json -Compress
exit 0
