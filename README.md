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

**Latency injection** comes in two kinds. Set the settings below as app pool environment variables or in `appsettings.json`.
- **HTTP:** before the handler runs, matching the reference app. Settings: `LATENCY_PROBABILITY` (default `0.05`) and `LATENCY_MAX_MS` (default `1300`).
- **Database:** a server-side `WAITFOR DELAY` before the SQL runs, so the slow query is also visible inside SQL Server. Settings: `DB_LATENCY_PROBABILITY` (default `0.03`), `DB_LATENCY_MIN_MS` (default `500`) and `DB_LATENCY_MAX_MS` (default `3000`).

Both kinds record `app.latency_injection.*` with a `latency.source` label set to `http` or `database`.

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

## Infrastructure telemetry and correlation

The collector also scrapes **IIS** (the `iis` receiver) and **SQL Server** (the `sqlserver` receiver, which uses perf counters plus a direct connection). See `deploy/otelcol-app-pipelines.yaml`. These are tied to the app in the following ways:

| Link | How |
|---|---|
| Common labels | All app, IIS and SQL telemetry carries `env_name=win`, `deployment.environment.name=win` and `host.name`. No `service.namespace` is set, because groundcover would treat it as a Kubernetes namespace. |
| IIS → app | IIS metrics for the `books-dotnet` site or app pool get `service.name=books-dotnet`. Server-wide IIS metrics get `service.name=iis`. |
| SQL Server → trace | Every app command first runs `SET CONTEXT_INFO <traceparent>`. The `sqlserver` receiver parses `CONTEXT_INFO` as a W3C traceparent, so its `db.server.query_sample` logs carry the request's `trace_id` and `span_id`. A slow query seen inside SQL Server, with its wait type and blocking session, links straight to the trace, the SqlClient span and the app logs. |
| Metrics → trace | `OTEL_METRICS_EXEMPLAR_FILTER=trace_based` attaches trace-ID exemplars to the app's histograms. |

Query samples only capture statements that are running at scrape time (every 10 s). Fast queries rarely show up; injected database latency makes them show up regularly.

The receiver logs in as the collector's service account (`NT AUTHORITY\SYSTEM`, Windows auth, no password) over TCP `127.0.0.1:1433`. See `deploy/configure-sql-monitoring.ps1`. The collector's own monitoring queries are filtered out of the SQL Server logs.

## Deviations from semantic conventions

The SqlClient instrumentation (agent v1.17.0) emits only the **stable** database semantic conventions: `db.system.name=microsoft.sql_server`, `server.address`, `db.namespace`, `db.query.text` and `db.query.summary`. Its `OTEL_SEMCONV_STABILITY_OPT_IN=database/dup` option, which emitted the old attributes alongside the new ones, was removed in 1.14.

The Python reference app's psycopg2 instrumentation still emits the **legacy** ones (`db.system`, `net.peer.name`, `db.name`). groundcover derives a span's database type (`protocol_type`) from the legacy `db.system`. Without it, books-dotnet's SQL spans showed no database type.

`transform/sqlclient_spans` in the collector changes these SqlClient spans:

| Change | Convention status | Why |
|---|---|---|
| Adds `db.system=mssql` when `db.system.name=microsoft.sql_server` | **Deviation**: a deprecated attribute, added next to the stable one | groundcover then classifies the span as `mssql`. |
| Removes the leading `SET CONTEXT_INFO @traceparent;` from `db.query.text` | **Deviation** | The app adds this line to every command so SQL Server telemetry can be tied to traces (see above). It isn't part of the app's query. Strictly, `db.query.text` should be the full text sent to the server, so this attribute no longer matches the wire exactly. An injected `WAITFOR DELAY` stays visible because it really is part of the query. |
| Sets `service.peer.name=sqlserver`, plus the deprecated `peer.service`, on calls to this host's SQL Server | **Follows the convention**: a peer-service mapping | Both attributes are defined as the remote service's `service.name`. `sqlserver` is the `service.name` the `sqlserver` receiver reports, so groundcover's `server` field on the span names the same entity it lists under Workloads. This is the collector-side equivalent of the Java agent's `peer-service-mapping`. |

`server.address` is left as SqlClient reports it: the host from the connection string. `deploy-iis.ps1` sets the connection string to `Server=<COMPUTERNAME>\SQLEXPRESS` rather than `.`, which still uses shared memory. The peer-service mapping matches on that host name.

**Not done:** groundcover also fills `partner_workload` from the legacy `net.peer.name`. The only way to get `sqlserver` there would be `net.peer.name=sqlserver`, which puts a service name in a hostname attribute. The OTel-instrumented services elsewhere in this workspace (for example frontend → cart) link through `server` with an empty `partner_workload`, so this deployment does the same.

If groundcover starts reading `db.system.name`, remove the added `db.system`.

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

# 4. Collector: enable SQL loopback TCP and the collector login, then merge
#    deploy/otelcol-app-pipelines.yaml into the collector's config.yaml, validate and restart.
.\deploy\configure-sql-monitoring.ps1

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
