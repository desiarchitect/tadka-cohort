namespace Tadka.Api.Domain.Restaurants;

/// <summary>
/// DEMO LEVER (Day 11, ADR-045): the restaurant's accept/reject decision on a confirmed payment.
/// Restaurant is not its own service until Day 12 (ADR-032) — today this decision is made in-process,
/// right where the monolith would otherwise auto-confirm the order after payment settles.
/// </summary>
public sealed class RestaurantAcceptanceOptions
{
    public const string SectionName = "Restaurant";

    /// <summary><c>Auto</c> (default): every order is accepted, exactly like Day 9. <c>Reject</c>:
    /// every order is rejected after payment — the compensation break/fix demo.</summary>
    public string AcceptMode { get; set; } = "Auto";

    /// <summary>DEMO LEVER: when a restaurant rejects, should the compensating refund actually fire?
    /// Default true (the correct behaviour ships by default). Set false to reproduce the break: the
    /// order cancels but the ALREADY-COMPLETED payment is never told — money stuck, forever.</summary>
    public bool RefundOnReject { get; set; } = true;
}
