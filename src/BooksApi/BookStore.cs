using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;

namespace BooksApi;

public sealed record Book(
    int Id,
    string Title,
    string Author,
    string Isbn,
    string? Description,
    decimal Price,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

// ---------------------------------------------------------------------------
// Database access (plain ADO.NET so SqlClient spans show the real SQL),
// instrumented with connection and query duration metrics.
// ---------------------------------------------------------------------------
public sealed class BookStore(IConfiguration config, IHttpContextAccessor http, ILogger<BookStore> logger)
{
    private const string Columns = "id, title, author, isbn, description, price, created_at, updated_at";

    // Columns a PUT may change; also the whitelist for building the SET clause.
    public static readonly string[] UpdatableFields = ["title", "author", "isbn", "description", "price"];

    private readonly string _connectionString = config.GetConnectionString("Books")
        ?? throw new InvalidOperationException("Missing connection string 'Books'");

    // Database-side latency injection: a server-side WAITFOR before the query, so the
    // slow query is visible both on the SqlClient span and inside SQL Server itself
    // (query samples, wait stats).
    private readonly double _dbLatencyProbability = config.GetValue("DB_LATENCY_PROBABILITY", 0.03);
    private readonly int _dbLatencyMinMs = config.GetValue("DB_LATENCY_MIN_MS", 500);
    private readonly int _dbLatencyMaxMs = config.GetValue("DB_LATENCY_MAX_MS", 3000);

    /// <summary>
    /// Create a command that (1) stamps the session's CONTEXT_INFO with the current W3C
    /// traceparent — the collector's sqlserver receiver parses it and puts the trace_id on
    /// its query-sample logs — and (2) occasionally injects database latency.
    /// </summary>
    private SqlCommand CreateCommand(string sql, SqlConnection conn)
    {
        var cmd = new SqlCommand { Connection = conn };
        var prefix = new StringBuilder();

        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceparent })
        {
            prefix.AppendLine("SET CONTEXT_INFO @traceparent;");
            cmd.Parameters.Add("@traceparent", SqlDbType.VarBinary, 128).Value = Encoding.ASCII.GetBytes(traceparent);
        }

        if (Random.Shared.NextDouble() < _dbLatencyProbability)
        {
            var delayMs = Random.Shared.Next(_dbLatencyMinMs, _dbLatencyMaxMs + 1);
            var tags = Telemetry.Tags(
                ("http.route", CurrentRoute()),
                ("http.method", http.HttpContext?.Request.Method),
                ("latency.source", "database"));

            logger.LogInformation("[Latency Injector] Delaying database query by {DelayMs} ms", delayMs);
            Telemetry.LatencyInjectionCount.Add(1, tags);
            Telemetry.LatencyInjectionDuration.Record(delayMs, tags);
            prefix.AppendLine("WAITFOR DELAY @delay;");
            cmd.Parameters.Add("@delay", SqlDbType.VarChar, 12).Value =
                TimeSpan.FromMilliseconds(delayMs).ToString(@"hh\:mm\:ss\.fff");
        }

        cmd.CommandText = prefix.Append(sql).ToString();
        return cmd;
    }

    /// <summary>Open a connection, recording connection time as a metric.</summary>
    private async Task<SqlConnection> OpenAsync()
    {
        var start = Stopwatch.GetTimestamp();
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync();
            Telemetry.DbConnectionDuration.Record(Telemetry.ElapsedMs(start),
                Telemetry.Tags(("db.system", Telemetry.DbSystem)));
            return conn;
        }
        catch (Exception ex)
        {
            await conn.DisposeAsync();
            Telemetry.DbConnectionDuration.Record(Telemetry.ElapsedMs(start),
                Telemetry.Tags(("db.system", Telemetry.DbSystem), ("error", true)));
            Telemetry.AppErrors.Add(1, Telemetry.Tags(
                ("error.type", "database_connection"),
                ("http.route", CurrentRoute())));
            logger.LogError(ex, "Error connecting to database: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>Execute a query and record its duration as a metric.</summary>
    private static async Task<T> TimedAsync<T>(string operation, Func<Task<T>> query)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await query();
            Telemetry.DbQueryDuration.Record(Telemetry.ElapsedMs(start),
                Telemetry.Tags(("db.system", Telemetry.DbSystem), ("db.operation", operation)));
            return result;
        }
        catch
        {
            Telemetry.DbQueryDuration.Record(Telemetry.ElapsedMs(start),
                Telemetry.Tags(("db.system", Telemetry.DbSystem), ("db.operation", operation), ("error", true)));
            throw;
        }
    }

    public async Task PingAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand("SELECT 1", conn);
        await TimedAsync("health_check", () => cmd.ExecuteScalarAsync());
    }

    public async Task<List<Book>> ListAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand($"SELECT {Columns} FROM dbo.Books ORDER BY id", conn);
        return await TimedAsync("select", () => ReadBooksAsync(cmd));
    }

    public async Task<Book?> GetAsync(int id)
    {
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand($"SELECT {Columns} FROM dbo.Books WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return (await TimedAsync("select", () => ReadBooksAsync(cmd))).SingleOrDefault();
    }

    public async Task<Book> CreateAsync(string title, string author, string isbn, string description, decimal price)
    {
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand(
            $"""
            INSERT INTO dbo.Books (title, author, isbn, description, price)
            OUTPUT {Prefixed("INSERTED")}
            VALUES (@title, @author, @isbn, @description, @price)
            """, conn);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@isbn", isbn);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@price", price);
        return (await TimedAsync("insert", () => ReadBooksAsync(cmd))).Single();
    }

    /// <param name="fields">Keys must come from <see cref="UpdatableFields"/>.</param>
    public async Task<Book?> UpdateAsync(int id, IReadOnlyDictionary<string, object> fields)
    {
        var setClause = string.Join(", ", fields.Keys.Select(f => $"{f} = @{f}"));
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand(
            $"""
            UPDATE dbo.Books
            SET {setClause}, updated_at = SYSUTCDATETIME()
            OUTPUT {Prefixed("INSERTED")}
            WHERE id = @id
            """, conn);
        foreach (var (field, value) in fields)
            cmd.Parameters.AddWithValue($"@{field}", value);
        cmd.Parameters.AddWithValue("@id", id);
        return (await TimedAsync("update", () => ReadBooksAsync(cmd))).SingleOrDefault();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var conn = await OpenAsync();
        await using var cmd = CreateCommand("DELETE FROM dbo.Books OUTPUT DELETED.id WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await TimedAsync("delete", () => cmd.ExecuteScalarAsync()) is not null;
    }

    private static string Prefixed(string table) =>
        string.Join(", ", Columns.Split(", ").Select(c => $"{table}.{c}"));

    private static async Task<List<Book>> ReadBooksAsync(SqlCommand cmd)
    {
        var books = new List<Book>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            books.Add(new Book(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDecimal(5),
                DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                reader.IsDBNull(7) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        }
        return books;
    }

    private string CurrentRoute()
    {
        var ctx = http.HttpContext;
        return (ctx?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? ctx?.Request.Path.Value
            ?? "unknown";
    }
}
