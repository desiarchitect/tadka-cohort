namespace Tadka.Restaurant.Api;

/// <summary>Restaurant service levers (ADR-062 accept/reject + ADR-061 canary buggy).</summary>
public sealed class RestaurantOptions
{
    public const string SectionName = "Restaurant";

    /// <summary><c>Auto</c> | <c>Reject</c> — decision on each order-confirmed.</summary>
    public string AcceptMode { get; set; } = "Auto";

    /// <summary>When true, GET list/menu returns 500 (canary destination).</summary>
    public bool Buggy { get; set; }
}
