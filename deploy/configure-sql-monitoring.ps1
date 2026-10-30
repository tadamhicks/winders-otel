# Prepares SQL Express for the collector's sqlserver receiver (direct connection):
#   - TCP enabled on 127.0.0.1:1433 only (the app still uses shared memory via .\SQLEXPRESS)
#   - Windows login for the collector's service account (LocalSystem) with the
#     minimum permissions the receiver needs. No SQL password anywhere.
# Restarts the SQL Server service if the network config changed. Idempotent.
param(
    [string]$Instance = 'SQLEXPRESS',
    [int]$Port = 1433,
    [string]$CollectorLogin = 'NT AUTHORITY\SYSTEM'
)
$ErrorActionPreference = 'Stop'
$sqlcmd = (Get-ChildItem 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\*\Tools\Binn\SQLCMD.EXE' | Select-Object -First 1).FullName

# --- TCP on loopback only ---
$id = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').$Instance
$tcp = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$id\MSSQLServer\SuperSocketNetLib\Tcp"
$changed = $false
function Set-Reg($path, $name, $value) {
    if ((Get-ItemProperty $path -Name $name -ErrorAction SilentlyContinue).$name -ne $value) {
        Set-ItemProperty $path -Name $name -Value $value; $script:changed = $true
    }
}
Set-Reg $tcp Enabled 1
Set-Reg $tcp ListenOnAllIPs 0
foreach ($key in Get-ChildItem $tcp | Where-Object PSChildName -like 'IP*' | Where-Object PSChildName -ne 'IPAll') {
    $loopback = (Get-ItemProperty $key.PSPath).IpAddress -eq '127.0.0.1'
    Set-Reg $key.PSPath Enabled ([int]$loopback)
    Set-Reg $key.PSPath TcpPort ($(if ($loopback) { "$Port" } else { '' }))
    Set-Reg $key.PSPath TcpDynamicPorts ''
}
Set-Reg "$tcp\IPAll" TcpPort ''
Set-Reg "$tcp\IPAll" TcpDynamicPorts ''
if ($changed) {
    "TCP config changed; restarting MSSQL`$$Instance"
    Restart-Service "MSSQL`$$Instance" -Force
}

# --- Collector login + permissions (SQL Server 2022+ names) ---
& $sqlcmd -S ".\$Instance" -E -C -b -Q @"
IF SUSER_ID(N'$CollectorLogin') IS NULL CREATE LOGIN [$CollectorLogin] FROM WINDOWS;
GRANT VIEW SERVER PERFORMANCE STATE TO [$CollectorLogin];
GRANT VIEW ANY DATABASE TO [$CollectorLogin];
GRANT VIEW ANY DEFINITION TO [$CollectorLogin];
"@
if ($LASTEXITCODE -ne 0) { throw 'granting collector permissions failed' }

netstat -ano | Select-String "127.0.0.1:$Port .*LISTENING"
