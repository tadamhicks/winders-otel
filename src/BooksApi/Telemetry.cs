using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BooksApi;

// ---------------------------------------------------------------------------
// Custom metrics instruments
//
// Uses only System.Diagnostics.Metrics (no OpenTelemetry package dependency).
// The OpenTelemetry .NET zero-code agent exports this Meter because its name
// is listed in OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES.
//
// Names and labels mirror the books-lambda (Python) reference app:
//   workload         — hardcoded "books-dotnet"; present on every metric
//   db.system        — OTel semantic convention for database type
//   db.operation     — select/insert/update/delete/health_check
//   http.method      — matches http_request_method on auto-instrumented metrics
//   http.route       — ASP.NET Core route template (e.g. /books/{id:int}),
//                      matching http.route on auto-instrumented spans
//   http.status_code — response status code as a string
//   error.type       — validation, database_connection, database_query,
//                      integrity_violation, health_check
// ---------------------------------------------------------------------------
public static class Telemetry
{
    public const string Workload = "books-dotnet";
    public const string Version = "2.0.0";
    public const string DbSystem = "mssql";

    public static readonly Meter Meter = new(Workload, Version);

    public static readonly Histogram<double> DbConnectionDuration = Meter.CreateHistogram<double>(
        "db.client.connection.duration", "ms", "Time to establish a database connection");

    public static readonly Histogram<double> DbQueryDuration = Meter.CreateHistogram<double>(
        "db.client.query.duration", "ms", "Database query execution time");

    public static readonly Counter<long> AppErrors = Meter.CreateCounter<long>(
        "app.errors", "1", "Application errors by type, route, and status code");

    public static readonly Counter<long> LatencyInjectionCount = Meter.CreateCounter<long>(
        "app.latency_injection.count", "1", "Number of times artificial latency was injected");

    public static readonly Histogram<double> LatencyInjectionDuration = Meter.CreateHistogram<double>(
        "app.latency_injection.duration", "ms", "Duration of injected artificial latency");

    public static readonly Histogram<long> BooksResultCount = Meter.CreateHistogram<long>(
        "app.books.result_count", "1", "Number of books returned per GET /books request");

    public static readonly Counter<long> BookOperations = Meter.CreateCounter<long>(
        "app.books.operations", "1", "Book CRUD operations by type and route");

    /// <summary>Base labels present on every custom metric, merged with per-call labels.</summary>
    public static TagList Tags(params (string Key, object? Value)[] extra)
    {
        var tags = new TagList { { "workload", Workload } };
        foreach (var (key, value) in extra)
            tags.Add(key, value);
        return tags;
    }

    public static void RecordError(string errorType, string route, int statusCode, string method = "GET") =>
        AppErrors.Add(1, Tags(
            ("error.type", errorType),
            ("http.route", route),
            ("http.status_code", statusCode.ToString()),
            ("http.method", method)));

    public static void RecordOperation(string operation, string route, string method = "GET") =>
        BookOperations.Add(1, Tags(
            ("operation", operation),
            ("http.route", route),
            ("http.method", method)));

    public static double ElapsedMs(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
