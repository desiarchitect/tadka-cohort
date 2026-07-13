using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace Tadka.Gateway.Canary;

/// <summary>
/// Weighted pick between destinations named "stable" and "canary" (ADR-061).
/// Percentage comes from <see cref="CanaryOptions.RestaurantPercent"/> (hot-reload via IOptionsMonitor).
/// </summary>
public sealed class WeightedCanaryPolicy(IOptionsMonitor<CanaryOptions> options) : ILoadBalancingPolicy
{
    public string Name => "WeightedCanary";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0) return null;
        if (availableDestinations.Count == 1) return availableDestinations[0];

        var percent = Math.Clamp(options.CurrentValue.RestaurantPercent, 0, 100);
        var canary = availableDestinations.FirstOrDefault(d =>
            d.Model.Config.Address.Contains("5261", StringComparison.Ordinal)
            || d.DestinationId.Contains("canary", StringComparison.OrdinalIgnoreCase));
        var stable = availableDestinations.FirstOrDefault(d => d != canary) ?? availableDestinations[0];

        if (canary is null) return stable;
        if (percent <= 0) return stable;
        if (percent >= 100) return canary;

        var roll = Random.Shared.Next(0, 100);
        return roll < percent ? canary : stable;
    }
}
