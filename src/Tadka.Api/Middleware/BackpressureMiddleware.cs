using Microsoft.AspNetCore.Mvc;

namespace Tadka.Api.Middleware;

/// <summary>
/// Admission control (ADR-059): caps concurrent in-flight requests. When the limit is hit, reject
/// immediately with 429 + Retry-After instead of queuing until every thread and connection is burned.
/// Demo lever: <c>Backpressure:MaxConcurrent</c> (0 = disabled).
/// </summary>
public sealed class BackpressureMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _maxConcurrent;
    private readonly SemaphoreSlim _gate;

    public BackpressureMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _maxConcurrent = config.GetValue("Backpressure:MaxConcurrent", 0);
        _gate = _maxConcurrent > 0
            ? new SemaphoreSlim(_maxConcurrent, _maxConcurrent)
            : new SemaphoreSlim(1, 1); // unused when disabled
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_maxConcurrent <= 0)
        {
            await _next(context);
            return;
        }

        if (!await _gate.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "1";
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Server Busy",
                Detail = $"Admission control: max concurrent requests is {_maxConcurrent}. Retry shortly.",
                Type = "https://tools.ietf.org/html/rfc6585#section-4"
            });
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            _gate.Release();
        }
    }
}
