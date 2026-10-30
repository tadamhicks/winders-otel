using System.Text.Json;
using BooksApi;
using Microsoft.Data.SqlClient;

// Telemetry export (traces, metrics, logs) is done by the OpenTelemetry .NET
// zero-code agent attached to the IIS worker process — see deploy/. This app
// only emits through .NET's built-in APIs: ILogger, System.Diagnostics.Metrics.

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<BookStore>();

var app = builder.Build();

app.UseExceptionHandler(e => e.Run(ctx =>
    Results.Json(new { error = "Internal server error" }, statusCode: 500).ExecuteAsync(ctx)));

const string BooksRoute = "/books";
const string BookRoute = "/books/{id:int}";
var service = new { service = Telemetry.Workload, version = Telemetry.Version };

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------
app.MapGet("/healthz", async (BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("Health check started");
    try
    {
        await store.PingAsync();
        logger.LogInformation("Health check completed successfully");
        return Results.Ok(new { status = "healthy", service.service, service.version, database = "connected" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Health check failed: {Message}", ex.Message);
        Telemetry.RecordError("health_check", "/healthz", 500);
        return Results.Json(new
        {
            status = "unhealthy", service.service, service.version, database = "disconnected", error = ex.Message,
        }, statusCode: 500);
    }
}).AddEndpointFilter<LatencyInjectionFilter>();

app.MapGet("/test", () =>
    Results.Ok(new { status = "ok", message = "Test endpoint working", service.service, service.version }))
    .AddEndpointFilter<LatencyInjectionFilter>();

app.MapGet(BooksRoute, async (BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("GET /books - Retrieving all books");
    try
    {
        var books = await store.ListAsync();
        Telemetry.BooksResultCount.Record(books.Count,
            Telemetry.Tags(("http.route", BooksRoute), ("http.method", "GET")));
        Telemetry.RecordOperation("list", BooksRoute);
        return Results.Ok(books);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error getting books: {Message}", ex.Message);
        Telemetry.RecordError("database_query", BooksRoute, 500);
        return Results.Json(new { error = "Failed to get books", message = ex.Message }, statusCode: 500);
    }
}).AddEndpointFilter<LatencyInjectionFilter>();

app.MapGet(BookRoute, async (int id, BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("GET /books/{BookId} - Retrieving book", id);
    try
    {
        var book = await store.GetAsync(id);
        Telemetry.RecordOperation("get", BookRoute);
        return book is null ? Results.NotFound(new { error = "Book not found" }) : Results.Ok(book);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error getting book {BookId}: {Message}", id, ex.Message);
        Telemetry.RecordError("database_query", BookRoute, 500);
        return Results.Json(new { error = "Failed to get book", message = ex.Message }, statusCode: 500);
    }
}).AddEndpointFilter<LatencyInjectionFilter>();

app.MapPost(BooksRoute, async (Dictionary<string, JsonElement> data, BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("POST /books - Creating new book");
    foreach (var field in BookStore.UpdatableFields)
    {
        if (!data.ContainsKey(field))
        {
            Telemetry.RecordError("validation", BooksRoute, 400, "POST");
            return Results.BadRequest(new { error = "Missing required field", field });
        }
    }
    if (!TryGetPrice(data["price"], out var price))
    {
        Telemetry.RecordError("validation", BooksRoute, 400, "POST");
        return Results.BadRequest(new { error = "Invalid price", field = "price" });
    }

    try
    {
        var book = await store.CreateAsync(
            Text(data["title"]), Text(data["author"]), Text(data["isbn"]), Text(data["description"]), price);
        Telemetry.RecordOperation("create", BooksRoute, "POST");
        return Results.Created($"/books/{book.Id}", book);
    }
    catch (SqlException ex) when (IsDuplicateKey(ex))
    {
        logger.LogWarning("Database integrity error: {Message}", ex.Message);
        Telemetry.RecordError("integrity_violation", BooksRoute, 409, "POST");
        return Results.Conflict(new { error = "Book with this ISBN already exists", message = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error creating book: {Message}", ex.Message);
        Telemetry.RecordError("database_query", BooksRoute, 500, "POST");
        return Results.Json(new { error = "Failed to create book", message = ex.Message }, statusCode: 500);
    }
});

app.MapPut(BookRoute, async (int id, Dictionary<string, JsonElement> data, BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("PUT /books/{BookId} - Updating book", id);
    var fields = new Dictionary<string, object>();
    foreach (var field in BookStore.UpdatableFields)
    {
        if (!data.TryGetValue(field, out var value)) continue;
        if (field != "price") fields[field] = Text(value);
        else if (TryGetPrice(value, out var price)) fields[field] = price;
        else
        {
            Telemetry.RecordError("validation", BookRoute, 400, "PUT");
            return Results.BadRequest(new { error = "Invalid price", field = "price" });
        }
    }
    if (fields.Count == 0)
    {
        Telemetry.RecordError("validation", BookRoute, 400, "PUT");
        return Results.BadRequest(new { error = "No fields to update" });
    }

    try
    {
        var book = await store.UpdateAsync(id, fields);
        Telemetry.RecordOperation("update", BookRoute, "PUT");
        return book is null ? Results.NotFound(new { error = "Book not found" }) : Results.Ok(book);
    }
    catch (SqlException ex) when (IsDuplicateKey(ex))
    {
        logger.LogWarning("Database integrity error: {Message}", ex.Message);
        Telemetry.RecordError("integrity_violation", BookRoute, 409, "PUT");
        return Results.Conflict(new { error = "Book with this ISBN already exists", message = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error updating book {BookId}: {Message}", id, ex.Message);
        Telemetry.RecordError("database_query", BookRoute, 500, "PUT");
        return Results.Json(new { error = "Failed to update book", message = ex.Message }, statusCode: 500);
    }
});

app.MapDelete(BookRoute, async (int id, BookStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("DELETE /books/{BookId} - Deleting book", id);
    try
    {
        var deleted = await store.DeleteAsync(id);
        Telemetry.RecordOperation("delete", BookRoute, "DELETE");
        return deleted
            ? Results.Ok(new { message = "Book deleted successfully", id })
            : Results.NotFound(new { error = "Book not found" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error deleting book {BookId}: {Message}", id, ex.Message);
        Telemetry.RecordError("database_query", BookRoute, 500, "DELETE");
        return Results.Json(new { error = "Failed to delete book", message = ex.Message }, statusCode: 500);
    }
});

app.MapFallback(() => Results.NotFound(new { error = "Not found" }));

app.Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static string Text(JsonElement value) =>
    value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

static bool TryGetPrice(JsonElement value, out decimal price)
{
    price = 0;
    return value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDecimal(out price),
        JsonValueKind.String => decimal.TryParse(value.GetString(), out price),
        _ => false,
    };
}

// 2627: unique constraint violation, 2601: unique index violation
static bool IsDuplicateKey(SqlException ex) => ex.Number is 2627 or 2601;
