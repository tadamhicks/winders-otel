# Publishes BooksApi and (re)configures its IIS app pool + site. Idempotent.
# Prereqs: IIS, .NET 10 Hosting Bundle, OpenTelemetry .NET auto-instrumentation
# registered for IIS (Register-OpenTelemetryForIIS), SQL Express (install-sqlexpress.ps1).
param(
    [string]$Name = 'books-dotnet',
    [string]$PhysicalPath = 'C:\inetpub\books-dotnet',
    [string]$Binding = '127.0.0.1:8080:'
)
$ErrorActionPreference = 'Stop'
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
$repo = Split-Path $PSScriptRoot

function AppCmd { & $appcmd @args; if ($LASTEXITCODE -ne 0) { throw "appcmd $args failed ($LASTEXITCODE)" } }
function Exists($type, $n) { & $appcmd list $type $n 2>$null | Out-Null; $LASTEXITCODE -eq 0 }

# --- App pool (No Managed Code; ASP.NET Core runs in-process via ANCM) ---
if (-not (Exists apppool $Name)) { AppCmd add apppool /name:$Name /managedRuntimeVersion:"" /startMode:AlwaysRunning }
if ((& $appcmd list apppool $Name /text:state) -eq 'Started') { AppCmd stop apppool /apppool.name:$Name }

# --- Publish ---
& "$env:ProgramFiles\dotnet\dotnet.exe" publish "$repo\src\BooksApi" -c Release -o $PhysicalPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- OpenTelemetry settings for this pool only ---
# Profiler/startup-hook variables come from Register-OpenTelemetryForIIS (WAS/W3SVC).
# App-pool environmentVariables are set when w3wp starts, before the agent initialises.
$otelEnv = [ordered]@{
    OTEL_SERVICE_NAME                               = $Name
    OTEL_RESOURCE_ATTRIBUTES                        = 'service.version=2.0.0,deployment.environment.name=win'
    OTEL_EXPORTER_OTLP_ENDPOINT                     = 'http://127.0.0.1:4318'   # collector listens on IPv4 loopback only
    OTEL_EXPORTER_OTLP_PROTOCOL                     = 'http/protobuf'
    OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES     = $Name                     # the app's custom Meter
    OTEL_DOTNET_AUTO_LOGS_INCLUDE_FORMATTED_MESSAGE = 'true'
}
$section = 'system.applicationHost/applicationPools'
foreach ($key in $otelEnv.Keys) {
    & $appcmd set config "-section:$section" /-"[name='$Name'].environmentVariables.[name='$key']" /commit:apphost 2>$null | Out-Null
    AppCmd set config "-section:$section" /+"[name='$Name'].environmentVariables.[name='$key',value='$($otelEnv[$key])']" /commit:apphost
}

# --- Site (localhost only) ---
if (-not (Exists site $Name)) {
    AppCmd add site /name:$Name /bindings:"http/$Binding" /physicalPath:$PhysicalPath
    AppCmd set app "$Name/" /applicationPool:$Name
}
AppCmd start apppool /apppool.name:$Name
if ((& $appcmd list site $Name /text:state) -ne 'Started') { AppCmd start site /site.name:$Name }

"Deployed $Name -> http://$($Binding.TrimEnd(':'))"
