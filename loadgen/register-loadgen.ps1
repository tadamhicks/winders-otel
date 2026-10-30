# Registers a scheduled task that runs loadgen.ps1 every 5 minutes (each run lasts
# ~290 s, so traffic is effectively continuous). Runs as SYSTEM, no logon needed.
# Remove with: Unregister-ScheduledTask -TaskName books-dotnet-loadgen -Confirm:$false
param([string]$TaskName = 'books-dotnet-loadgen')
$ErrorActionPreference = 'Stop'

$pwsh = (Get-Command pwsh.exe).Source   # loadgen.ps1 uses PowerShell 7 features
$script = Join-Path $PSScriptRoot 'loadgen.ps1'
$action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -User 'SYSTEM' -RunLevel Highest -Description 'Synthetic traffic for books-dotnet (IIS, 127.0.0.1:8080)' -Force |
    Select-Object TaskName, State
