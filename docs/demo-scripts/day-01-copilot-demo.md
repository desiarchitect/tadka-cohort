# Day 1 — Copilot Agent Mode Demo Script

## Setup (Before the Session)

1. Open `Tadka.Api` in VS Code
2. Ensure Docker is running with PostgreSQL (`docker compose up -d`)
3. Ensure the API starts cleanly (`dotnet run` → `/health` returns 200)
4. Close all editor tabs except `Program.cs` (clean starting point)
5. Have Scalar UI open in browser (`/scalar/v1`) to refresh after demo

## Context for Students

> "We've got a basic health endpoint that just returns a timestamp. That's fine for 'is the process alive', but production apps need a readiness probe. Kubernetes, ECS, any orchestrator wants to know: can this instance actually serve traffic? That means checking dependencies. Right now our only dependency is PostgreSQL. Let's use Copilot Agent Mode to build a proper readiness check."

## Primary Demo Task

### The Prompt

Open Copilot Chat in Agent Mode (Ctrl+Shift+I → select Agent Mode) and type:

```
Add a readiness endpoint GET /health/ready to HealthController that checks PostgreSQL 
connectivity. It should attempt a simple query (SELECT 1), measure how long the query 
takes in milliseconds, and return a JSON response with:
- status: "ready" or "degraded" 
- database: { connected: true/false, latencyMs: number }
- timestamp: UTC timestamp

If the database query fails, return status "degraded" with connected: false and the 
error message. Use the existing TadkaDbContext.
```

### What to Point Out While Copilot Works

1. **"Watch it read the project first."** Copilot will scan `HealthController.cs`, `TadkaDbContext.cs`, `Program.cs`. It understands the existing codebase before writing code.

2. **"It's using constructor injection."** Copilot will inject `TadkaDbContext` into the controller. Point out: "It read `Program.cs`, saw we register `TadkaDbContext` via DI, and used it. It didn't create a new connection string."

3. **"Notice the try-catch."** The database might be down. A readiness probe that crashes on DB failure is useless. Copilot should handle the failure gracefully.

4. **"Look at the HTTP status code."** Healthy = 200, degraded = 503 (Service Unavailable). Orchestrators use this to route traffic away from unhealthy instances.

### Expected Output (approximately)

```csharp
[HttpGet("ready")]
public async Task<IActionResult> Ready([FromServices] TadkaDbContext dbContext)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await dbContext.Database.ExecuteSqlRawAsync("SELECT 1");
        stopwatch.Stop();

        return Ok(new
        {
            status = "ready",
            database = new
            {
                connected = true,
                latencyMs = stopwatch.ElapsedMilliseconds
            },
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        return StatusCode(503, new
        {
            status = "degraded",
            database = new
            {
                connected = false,
                error = ex.Message,
                latencyMs = stopwatch.ElapsedMilliseconds
            },
            timestamp = DateTime.UtcNow
        });
    }
}
```

### After Copilot Generates Code

1. **Review it together.** "Is this production-ready? What would you change?" Let students critique.
2. **Run it.** `dotnet run`, hit `/health/ready` in browser or Scalar UI.
3. **Show the happy path.** Database is up → `{"status": "ready", "database": {"connected": true, "latencyMs": 2}}`.
4. **Show the failure path.** Stop PostgreSQL (`docker compose stop postgres`), hit `/health/ready` → `{"status": "degraded", "database": {"connected": false, "error": "..."}}`.
5. **Restart PostgreSQL.** `docker compose start postgres`. Hit `/health/ready` → back to "ready".

### Key Teaching Moment

> "This is what Copilot is good at: boilerplate with context. It read the existing code, understood the DI setup, and produced a working endpoint in 30 seconds. Could you have written this yourself? Of course. But in a cohort where we're building a full food delivery platform, we don't want to spend time on health checks. We want to spend time on architecture decisions. Copilot handles the plumbing. You handle the thinking."

## Backup Demo Task

If Copilot is slow, unavailable, or produces poor output, switch to this:

### The Prompt

```
Create a middleware for Tadka.Api that logs the HTTP method, path, response status code, 
and elapsed time in milliseconds for every request. Register it in Program.cs.
```

### Expected Output (approximately)

```csharp
public class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        _logger.LogInformation("{Method} {Path} → {StatusCode} in {ElapsedMs}ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.ElapsedMilliseconds);
    }
}
```

### Why This Works as a Backup
- Simple, self-contained, doesn't depend on DB connection
- Shows Copilot understanding middleware pipeline pattern
- Produces visible output (check terminal logs after hitting any endpoint)

## Timing

| Step | Duration |
|------|----------|
| Context / why readiness probes | 2 min |
| Type prompt, watch Copilot work | 2-3 min |
| Review generated code together | 2 min |
| Run and test (happy + failure) | 3 min |
| **Total** | ~10 min |

## Prep Checklist

- [ ] Tested primary prompt on the exact monolith scaffold. Know the output.
- [ ] Tested backup prompt. Know the output.
- [ ] PostgreSQL running and connectable.
- [ ] Copilot extension updated to latest version.
- [ ] Agent Mode enabled (not just inline/chat).
- [ ] Scalar UI accessible at `/scalar/v1`.
- [ ] Know how to quickly restart PostgreSQL for the failure demo.
