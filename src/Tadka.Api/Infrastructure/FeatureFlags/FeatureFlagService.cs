using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.FeatureFlags;

/// <summary>
/// Production-shaped percentage rollout (ADR-058). Stable hash of (flagName, userKey) so the same user
/// stays in the same cohort when the percentage is raised. Redis-backed optional override
/// <c>Flags:{name}</c> as integer 0–100; falls back to configuration <c>Flags:{name}</c>.
/// </summary>
public interface IFeatureFlagService
{
    /// <summary>True when the user is in the enabled percentage for the named flag.</summary>
    Task<bool> IsEnabledAsync(string flagName, string userKey, CancellationToken ct = default);

    /// <summary>Current rollout percentage 0–100 (for demos / admin).</summary>
    Task<int> GetPercentAsync(string flagName, CancellationToken ct = default);
}

public sealed class FeatureFlagOptions
{
    public const string SectionName = "Flags";
    /// <summary>Default percents when Redis has no override. Keys are flag names.</summary>
    public Dictionary<string, int> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UseNewMenuPath"] = 0,
        ["CanaryRestaurant"] = 0
    };
}

public sealed class FeatureFlagService(
    IConfiguration config,
    IConnectionMultiplexer? redis,
    ILogger<FeatureFlagService> logger) : IFeatureFlagService
{
    public async Task<int> GetPercentAsync(string flagName, CancellationToken ct = default)
    {
        if (redis is not null)
        {
            try
            {
                var db = redis.GetDatabase();
                var raw = await db.StringGetAsync($"Flags:{flagName}");
                if (raw.HasValue && int.TryParse((string)raw!, out var fromRedis))
                    return Math.Clamp(fromRedis, 0, 100);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feature flag Redis read failed for {Flag}; using config.", flagName);
            }
        }

        var fromConfig = config.GetValue<int?>($"Flags:{flagName}");
        return Math.Clamp(fromConfig ?? 0, 0, 100);
    }

    public async Task<bool> IsEnabledAsync(string flagName, string userKey, CancellationToken ct = default)
    {
        var percent = await GetPercentAsync(flagName, ct);
        if (percent <= 0) return false;
        if (percent >= 100) return true;

        // Stable bucket 0..99 from SHA256(flag|user)
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{flagName}|{userKey}"));
        var bucket = bytes[0] % 100;
        return bucket < percent;
    }
}
