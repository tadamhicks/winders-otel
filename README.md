# books-dotnet

A .NET port of [books-lambda](https://github.com/tadamhicks/books-lambda). It is a small Books CRUD API used to demo OpenTelemetry. This version runs on Windows Server under **IIS**, stores data in **SQL Server Express**, and sends traces, metrics and logs to **groundcover** through the local OpenTelemetry Collector.

```
loadgen (Task Scheduler) ──HTTP──▶ IIS 127.0.0.1:8080 ──▶ ASP.NET Core (in-process, w3wp)
                                                         │   └─ SqlClient ──▶ SQL Express .\SQLEXPRESS / BooksDb
                                                         │
                                        OTel .NET zero-code agent (traces, metrics, logs)
                                                         │ OTLP http/protobuf
                                                         ▼
                                   otelcol-contrib 127.0.0.1:4318 ──▶ groundcover
```

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/healthz` | Runs `SELECT 1` against the DB. Latency injection applies. |
| GET | `/test` | No DB access. Latency injection applies. |
| GET | `/books` | Lists all books. Latency injection applies. |
| GET | `/books/{id}` | Latency injection applies. Returns 404 if the book doesn't exist. |
| POST | `/books` | Requires `title, author, isbn, description, price`. Returns 400 if a field is missing and 409 if the ISBN already exists. |
| PUT | `/books/{id}` | Partial update. Returns 400 if no fields are given and 409 if the ISBN already exists. |
| DELETE | `/books/{id}` | |

**Latency injection:** `LATENCY_PROBABILITY` (default `0.05`) and `LATENCY_MAX_MS` (default `1300`) control it. Set them as app pool environment variables or in `appsettings.json`.

## Instrumentation (hybrid)

- **Zero-code:** the [OpenTelemetry .NET auto-instrumentation](https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation) agent, v1.17.0, is registered for IIS. It produces:
  - traces for ASP.NET Core and SqlClient;
  - runtime, process and HTTP metrics;
  - log export from `ILogger`.
- **In code:** a `Meter` named `books-dotnet` (see `src/BooksApi/Telemetry.cs`) and `ILogger` calls. The app has no OpenTelemetry package references. The agent exports the Meter because `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES=books-dotnet` is set.

The custom metrics use the same names and labels as the reference app, with `workload=books-dotnet` and `db.system=mssql`:

| Metric | Type | Labels |
|---|---|---|
| `db.client.connection.duration` | histogram, ms | `db.system`, `error` |
| `db.client.query.duration` | histogram, ms | `db.system`, `db.operation`, `error` |
| `app.errors` | counter | `error.type`, `http.route`, `http.method`, `http.status_code` |
| `app.latency_injection.count` | counter | `http.route`, `http.method` |
| `app.latency_injection.duration` | histogram, ms | `http.route`, `http.method` |
| `app.books.result_count` | histogram | `http.route`, `http.method` |
| `app.books.operations` | counter | `operation`, `http.route`, `http.method` |

Per-app OTel settings are set as environment variables on the `books-dotnet` IIS app pool (see `deploy/deploy-iis.ps1`):
- `OTEL_SERVICE_NAME=books-dotnet`
- `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318`
- and the others listed in that script.

## Setup from scratch

Run these as an administrator on Windows Server.

```powershell
# 1. Prereqs
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.10 -e
Install-WindowsFeature Web-Server,Web-Mgmt-Console -IncludeManagementTools
winget install --id Microsoft.DotNet.HostingBundle.10 -e   # install after IIS, or repair it afterwards

# 2. SQL Server 2025 Express. winget extracts the media to C:\SQL2025\Express_ENU;
#    the script runs setup as SYSTEM, which avoids a DPAPI error in remote sessions.
winget install --id Microsoft.SQLServer.2025.Express -e    # may "fail", but the media is left behind
.\deploy\install-sqlexpress.ps1

# 3. OTel .NET agent for IIS
#    Use the module from the release; Install-OpenTelemetryCore, then Register-OpenTelemetryForIIS.

# 4. Collector: merge deploy/otelcol-app-pipelines.yaml into the collector's config.yaml, then validate and restart.

# 5. App + DB
.\deploy\deploy-iis.ps1
sqlcmd -S .\SQLEXPRESS -E -C -i db\schema.sql -v AppPoolLogin="IIS APPPOOL\books-dotnet"

# 6. Synthetic load
.\loadgen\register-loadgen.ps1
```

## Operations

- **Redeploy after code changes:** `.\deploy\deploy-iis.ps1`
- **Stop the load:** `Disable-ScheduledTask books-dotnet-loadgen`
- **Agent logs:** `C:\ProgramData\OpenTelemetry .NET AutoInstrumentation\logs`
- **Collector:** the `otelcol-contrib` service; health check at `http://127.0.0.1:13133`
