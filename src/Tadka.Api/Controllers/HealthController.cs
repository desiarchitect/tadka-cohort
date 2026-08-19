using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    private readonly TadkaDbContext _dbContext;

    public HealthController(TadkaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Liveness — the process is up. Does not talk to Postgres.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "Healthy",
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>Readiness — Postgres answers SELECT 1. 200 if healthy, 503 if not.</summary>
    [HttpGet("ready")]
    public async Task<IActionResult> Ready()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1");
            stopwatch.Stop();

            return Ok(new
            {
                status = "Healthy",
                database = "Connected",
                responseTime = $"{stopwatch.ElapsedMilliseconds}ms",
                timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            stopwatch.Stop();
            return StatusCode(503, new
            {
                status = "Unhealthy",
                database = "Disconnected",
                responseTime = $"{stopwatch.ElapsedMilliseconds}ms",
                timestamp = DateTime.UtcNow
            });
        }
    }
}
