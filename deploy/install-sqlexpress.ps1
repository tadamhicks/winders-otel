# Installs SQL Server 2025 Express (engine only, instance SQLEXPRESS) from media
# already extracted to C:\SQL2025\Express_ENU (by the SSEI bootstrapper / winget).
#
# SQL Setup encrypts its settings with DPAPI, which fails ("CryptographicException:
# Access is denied") when run from a non-interactive remote session without a loaded
# profile. So this script re-launches itself as SYSTEM via a one-shot scheduled task.
param(
    [string]$Media = 'C:\SQL2025\Express_ENU',
    [string]$SysAdmin = "$env:COMPUTERNAME\$env:USERNAME",
    [int]$MaxServerMemoryMB = 1024,
    [switch]$AsSystem
)
$ErrorActionPreference = 'Stop'

if (-not $AsSystem) {
    $log = "$env:ProgramData\books-dotnet\sql-install.log"
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AsSystem -Media `"$Media`" -SysAdmin `"$SysAdmin`" -MaxServerMemoryMB $MaxServerMemoryMB *> `"$log`""
    $task = 'books-dotnet-sql-install'
    Register-ScheduledTask -TaskName $task -Force -User 'SYSTEM' -RunLevel Highest `
        -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg) | Out-Null
    Start-ScheduledTask -TaskName $task
    do { Start-Sleep 15 } while ((Get-ScheduledTask -TaskName $task).State -eq 'Running')
    $result = (Get-ScheduledTaskInfo -TaskName $task).LastTaskResult
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    Get-Content $log
    if ($result -ne 0) { throw "SQL install task failed with $result (see $log)" }
    return
}

& "$Media\setup.exe" /Q /ACTION=Install /FEATURES=SQLENGINE /INSTANCENAME=SQLEXPRESS `
    /IACCEPTSQLSERVERLICENSETERMS /UPDATEENABLED=0 `
    /SQLSVCACCOUNT="NT Service\MSSQL`$SQLEXPRESS" /SQLSVCSTARTUPTYPE=Automatic `
    /SQLSYSADMINACCOUNTS="BUILTIN\Administrators" "$SysAdmin" /TCPENABLED=0
"setup.exe exit code: $LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Cap memory: this box only has ~4 GB shared with IIS and the OTel collector.
$sqlcmd = Get-ChildItem 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\*\Tools\Binn\SQLCMD.EXE' -ErrorAction SilentlyContinue | Select-Object -First 1
$cap = "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'max server memory (MB)', $MaxServerMemoryMB; RECONFIGURE;"
if ($sqlcmd) { & $sqlcmd.FullName -S '.\SQLEXPRESS' -E -C -Q $cap }
else {
    Add-Type -AssemblyName System.Data
    $c = New-Object System.Data.SqlClient.SqlConnection 'Server=.\SQLEXPRESS;Integrated Security=true'
    $c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = $cap; [void]$cmd.ExecuteNonQuery(); $c.Close()
}
"max server memory set to $MaxServerMemoryMB MB"
