namespace Tadka.Api.Domain.Restaurants;

/// <summary>
/// DEMO LEVERS for restaurant accept/reject + refund compensation (ADR-045).
/// <list type="bullet">
/// <item><c>DecisionMode=Inline</c> (default): Ordering decides at payment-settled time (tests / no Restaurant consumer).</item>
/// <item><c>DecisionMode=Service</c>: Ordering always confirms; Restaurant.Api consumes <c>order-confirmed</c>
/// and publishes <c>restaurant-response</c> (production multi-service path).</item>
/// </list>
/// </summary>
public sealed class RestaurantAcceptanceOptions
{
    public const string SectionName = "Restaurant";

    /// <summary><c>Inline</c> | <c>Service</c>. Service requires Kafka + Restaurant.Api consumer.</summary>
    public string DecisionMode { get; set; } = "Inline";

    /// <summary><c>Auto</c> (default) | <c>Reject</c> — compensation break/fix demo.</summary>
    public string AcceptMode { get; set; } = "Auto";

    /// <summary>When false: cancel order but do not emit refund-requested (money-stuck break).</summary>
    public bool RefundOnReject { get; set; } = true;

    public bool UseServiceDecision =>
        string.Equals(DecisionMode, "Service", StringComparison.OrdinalIgnoreCase);
}
