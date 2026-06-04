using System.Net.Http.Json;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Black-box tests of the extracted Payment service over its real HTTP contract (ADR-024/025): the charge
/// endpoint against a real Postgres, with the gateway behaviour dialed per-test. These are the Day-7
/// payment behaviours, now living with the service that owns them.
/// </summary>
public class PaymentServiceTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    private readonly PaymentApiFactory _factory = factory;

    private sealed record ChargeResponse(Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

    [Fact]
    public async Task FastGateway_completes_the_charge_and_returns_a_reference()
    {
        var client = _factory.CreateClient();
        var orderId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync("/payments/charge", new { orderId, amount = 299.00m, currency = "INR" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ChargeResponse>();

        Assert.Equal("Completed", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.GatewayReference));
    }

    [Fact]
    public async Task FailingGateway_returns_a_business_Failed_outcome_not_an_error()
    {
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Payment:Gateway:Behavior", "Failing")).CreateClient();
        var orderId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync("/payments/charge", new { orderId, amount = 299.00m, currency = "INR" });
        resp.EnsureSuccessStatusCode(); // a DECLINE is still HTTP 200 — a business outcome, not a transport error
        var body = await resp.Content.ReadFromJsonAsync<ChargeResponse>();

        Assert.Equal("Failed", body!.Status);
    }

    [Fact]
    public async Task SlowGateway_is_abandoned_by_the_Polly_timeout_and_fails_fast()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Payment:Gateway:Behavior", "Slow");
            b.UseSetting("Payment:Gateway:SlowDelaySeconds", "5");
            b.UseSetting("Payment:TimeoutSeconds", "0.2");
        }).CreateClient();
        var orderId = Guid.NewGuid();

        var start = DateTime.UtcNow;
        var resp = await client.PostAsJsonAsync("/payments/charge", new { orderId, amount = 299.00m, currency = "INR" });
        var elapsed = DateTime.UtcNow - start;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ChargeResponse>();

        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Expected fail-fast (~200ms), took {elapsed.TotalMilliseconds:N0}ms");
        Assert.Equal("Failed", body!.Status);
    }

    [Fact]
    public async Task ChargingTheSameOrderTwice_is_idempotent_one_payment_same_reference()
    {
        var client = _factory.CreateClient();
        var orderId = Guid.NewGuid();
        var payload = new { orderId, amount = 299.00m, currency = "INR" };

        var first = await (await client.PostAsJsonAsync("/payments/charge", payload)).Content.ReadFromJsonAsync<ChargeResponse>();
        var second = await (await client.PostAsJsonAsync("/payments/charge", payload)).Content.ReadFromJsonAsync<ChargeResponse>();

        Assert.Equal("Completed", first!.Status);
        Assert.Equal("Completed", second!.Status);
        Assert.Equal(first.GatewayReference, second.GatewayReference); // same charge, not a second one

        // GET confirms a single record exists for the order
        var got = await client.GetFromJsonAsync<ChargeResponse>($"/payments/{orderId}");
        Assert.Equal(first.GatewayReference, got!.GatewayReference);
    }
}
