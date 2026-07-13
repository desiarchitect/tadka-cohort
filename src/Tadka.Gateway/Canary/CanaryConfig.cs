namespace Tadka.Gateway.Canary;

/// <summary>
/// Weighted canary for the restaurant cluster (ADR-061). Percent of traffic to the "buggy" destination.
/// Set <c>Canary:RestaurantPercent</c> 0–100; destinations configured in ReverseProxy clusters.
/// </summary>
public sealed class CanaryOptions
{
    public const string SectionName = "Canary";
    /// <summary>0 = all stable; 100 = all canary; 5 = 5% canary (classic demo).</summary>
    public int RestaurantPercent { get; set; }
}
