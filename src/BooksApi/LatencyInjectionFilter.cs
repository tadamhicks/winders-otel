namespace BooksApi;

// ---------------------------------------------------------------------------
// Latency injection (chaos testing) — instrumented.
// Equivalent of the reference app's @maybe_delay decorator.
//   LATENCY_PROBABILITY  chance a request is delayed (default 0.05)
//   LATENCY_MAX_MS       upper bound of the delay; lower bound is 100 ms (default 1300)
// ---------------------------------------------------------------------------
public sealed class LatencyInjectionFilter(IConfiguration config, ILogger<LatencyInjectionFilter> logger) : IEndpointFilter
{
    private readonly double _probability = config.GetValue("LATENCY_PROBABILITY", 0.05);
    private readonly int _maxMs = config.GetValue("LATENCY_MAX_MS", 1300);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (Random.Shared.NextDouble() < _probability)
        {
            var http = context.HttpContext;
            var delayMs = Random.Shared.Next(100, _maxMs + 1);
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value;
            var tags = Telemetry.Tags(("http.route", route), ("http.method", http.Request.Method));

            logger.LogInformation("[Latency Injector] Delaying request by {DelayMs} ms", delayMs);
            Telemetry.LatencyInjectionCount.Add(1, tags);
            Telemetry.LatencyInjectionDuration.Record(delayMs, tags);
            await Task.Delay(delayMs, http.RequestAborted);
        }
        return await next(context);
    }
}
