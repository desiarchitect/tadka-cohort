using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using Tadka.Api.Infrastructure.FeatureFlags;

namespace Tadka.Api.Controllers;

/// <summary>Instructor/demo endpoints for percentage feature flags (ADR-058).</summary>
[ApiController]
[Route("api/v1/flags")]
public sealed class FeatureFlagsController(
    IFeatureFlagService flags,
    IConnectionMultiplexer? redis) : ControllerBase
{
    [HttpGet("{name}")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Get(string name, CancellationToken ct)
    {
        var percent = await flags.GetPercentAsync(name, ct);
        var userKey = User.Identity?.IsAuthenticated == true
            ? (User.FindFirst("sub")?.Value ?? "anon")
            : (Request.Headers.UserAgent.ToString() ?? "anon");
        var enabled = await flags.IsEnabledAsync(name, userKey, ct);
        return Ok(new { flag = name, percent, enabledForCaller = enabled, userKeyPreview = userKey[..Math.Min(12, userKey.Length)] });
    }

    /// <summary>Set rollout percent 0–100 (Admin). Writes Redis key Flags:{name} when Redis is available.</summary>
    [HttpPut("{name}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<object>> Set(string name, [FromBody] FlagPercentBody body, CancellationToken ct)
    {
        var percent = Math.Clamp(body.Percent, 0, 100);
        if (redis is null)
            return Problem(detail: "Redis not configured; set Flags:{name} in appsettings for this process only.", statusCode: 503);

        await redis.GetDatabase().StringSetAsync($"Flags:{name}", percent);
        return Ok(new { flag = name, percent });
    }
}

public sealed record FlagPercentBody(int Percent);
