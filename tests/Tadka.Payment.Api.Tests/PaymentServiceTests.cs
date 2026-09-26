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

    [Fact]
    public async Task Charge_without_a_token_is_rejected_401()  // per-service validation (ADR-031, defense in depth)
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments/charge")
        { Content = JsonContent.Create(new { orderId = Guid.NewGuid(), amount = 100m, currency = "INR" }) };
        req.Headers.Add("X-Test-NoAuth", "true");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task A_customer_cannot_trigger_a_charge_403()  // authenticated is not the same as authorized (ADR-031)
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments/charge")
        { Content = JsonContent.Create(new { orderId = Guid.NewGuid(), amount = 1.00m, currency = "INR" }) };
        req.Headers.Add("X-Test-Auth", $"Customer:{Guid.NewGuid()}");  // a real, valid, logged-in customer
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task A_different_customer_cannot_read_someone_elses_payment_403()  // resource ownership (ADR-031)
    {
        var client = _factory.CreateClient();
        var orderId = Guid.NewGuid();
        var owner = Guid.NewGuid();

        // The order-placed event stamps CustomerId on the payment row (Admin acting as "the system" here).
        var chargeReq = new HttpRequestMessage(HttpMethod.Post, "/payments/charge")
        { Content = JsonContent.Create(new { orderId, amount = 299.00m, currency = "INR", customerId = owner }) };
        (await client.SendAsync(chargeReq)).EnsureSuccessStatusCode();

        // A DIFFERENT customer tries to read it → 403.
        var asOther = Get($"/payments/{orderId}", $"Customer:{Guid.NewGuid()}");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(asOther)).StatusCode);

        // The owner CAN read it → 200.
        var asOwner = Get($"/payments/{orderId}", $"Customer:{owner}");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SendAsync(asOwner)).StatusCode);
    }

    private static HttpRequestMessage Get(string url, string testAuth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Test-Auth", testAuth);
        return req;
    }
}
