using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;

namespace Tadka.Api.Controllers;

/// <summary>
/// Liveness + readiness (same contract as satellite services).
/// <c>GET /health</c> — process up. <c>GET /health/ready</c> — DB reachable (k8s readiness probe).
/// </summary>
[ApiController]
public class HealthController(TadkaDbContext dbContext) : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Live() => Ok(new { status = "Healthy", service = "ordering", timestamp = DateTime.UtcNow });

    [HttpGet("/health/ready")]
    public async Task<IActionResult> Ready()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT 1");
            stopwatch.Stop();
            return Ok(new
            {
                status = "Ready",
                service = "ordering",
                database = "Connected",
                responseTime = $"{stopwatch.ElapsedMilliseconds}ms",
                timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            stopwatch.Stop();
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Detail = "Ordering database is unreachable.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4"
            });
        }
    }
}
