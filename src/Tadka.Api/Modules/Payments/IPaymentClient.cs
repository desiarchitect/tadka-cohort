using System.Net.Http.Json;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>The Payment service's outcome, as the monolith sees it. Mirrors the service's response shape —
/// each service owns its own copy of the contract (no shared code across the boundary, ADR-025).</summary>
public sealed record PaymentChargeResult(string Status, string? GatewayReference, string? FailureReason);

/// <summary>
/// The monolith's view of the extracted Payment service (ADR-024/025). Replaces the in-process
/// <c>PaymentService</c> that lived here on Day 7. Returns <c>null</c> when the service is UNREACHABLE
/// (down / network error / timeout) — a transport failure the caller must cope with; a non-null result is
/// a business outcome (Completed/Failed).
/// </summary>
public interface IPaymentClient
{
    Task<PaymentChargeResult?> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken);
}

/// <summary>
/// Typed <see cref="HttpClient"/> over <c>POST /payments/charge</c>, wrapped in the reused Day-7 Polly
/// pipeline (ADR-025). A DOWN service / a fired timeout surfaces as <c>null</c> — and because the queued
/// item is already consumed, that charge is lost and the order stays pending. We leave that gap visible:
/// it's exactly what Kafka + the Outbox pattern fix on Day 9.
///
/// <para><b>Hand-rolled for learning.</b> In production: register HTTP resilience declaratively via
/// <b>Microsoft.Extensions.Http.Resilience</b> (<c>AddStandardResilienceHandler</c>, Polly under the hood),
/// and prefer durable messaging (<b>MassTransit</b>) over a synchronous call for commands like this.</para>
/// </summary>
public sealed class HttpPaymentClient(
    HttpClient http,
    PaymentClientResilience resilience,
    ILogger<HttpPaymentClient> logger) : IPaymentClient
{
    public async Task<PaymentChargeResult?> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken)
    {
        try
        {
            var response = await resilience.Pipeline.ExecuteAsync(
                async token => await http.PostAsJsonAsync(
                    "/payments/charge",
                    new { orderId, amount = amount.Amount, currency = amount.Currency },
                    token),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Payment service returned {Status} for order {OrderId}; treating as unsettled.",
                    (int)response.StatusCode, orderId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PaymentChargeResult>(cancellationToken);
        }
        catch (Exception ex)
        {
            // Connection refused (service DOWN), DNS failure, or the Polly timeout firing — all TRANSPORT
            // failures. The order stays pending; the charge is lost from the in-memory queue. THIS is the
            // temporal coupling of synchronous HTTP — the cliffhanger that earns Kafka on Day 9 (ADR-025).
            logger.LogError(ex, "Payment service UNREACHABLE for order {OrderId} — order stays pending.", orderId);
            return null;
        }
    }
}
