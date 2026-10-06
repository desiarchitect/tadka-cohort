using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Payment.Api.Gateway;
using Tadka.Payment.Api.Resilience;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Pure unit tests for the fix 2 / ADR-043 Buffer-vs-Compensate branch in
/// <see cref="PaymentService.ChargeAsync"/> — EF Core InMemory provider, no Docker, no real Postgres,
/// no real Kafka. A hand-built <see cref="PaymentResiliencePipeline"/> plus a scripted fake gateway
/// stand in for the real dependency.
/// </summary>
public class PaymentServiceGatewayUnavailableTests
{
    private static PaymentDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentDbContext>().UseInMemoryDatabase(name).Options);

    private static PaymentResiliencePipeline NewPipeline(PaymentOptions options) =>
        new(Options.Create(options), NullLogger<PaymentResiliencePipeline>.Instance);

    private static PaymentOptions BaseOptions(GatewayUnavailableMode mode) => new()
    {
        MaxRetryAttempts = 0,        // isolate ChargeAsync's own branch, not the retry policy
        TimeoutSeconds = 5,
        MaxConcurrentCharges = 10,
        CircuitMinimumThroughput = 1000, // keep the breaker closed for these tests — we're testing the
                                          // transport-failure branch, not the breaker trip itself
        OnGatewayUnavailable = mode
    };

    /// <summary>Always throws PaymentGatewayUnavailableException — a transport failure, not a decline.</summary>
    private sealed class AlwaysUnavailableGateway : IPaymentGateway
    {
        public int CallCount { get; private set; }
        public Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken ct)
        {
            CallCount++;
            throw new PaymentGatewayUnavailableException("simulated gateway outage");
        }
        public Task<string> RefundAsync(Guid orderId, string? originalGatewayReference, Money amount, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>Fails on the first N calls (PaymentGatewayUnavailableException), then succeeds.</summary>
    private sealed class RecoveringGateway(int failuresBeforeSuccess) : IPaymentGateway
    {
        public int CallCount { get; private set; }
        public Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken ct)
        {
            CallCount++;
            if (CallCount <= failuresBeforeSuccess)
                throw new PaymentGatewayUnavailableException("simulated gateway outage");
            return Task.FromResult($"REF-{CallCount}");
        }
        public Task<string> RefundAsync(Guid orderId, string? originalGatewayReference, Money amount, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>Always declines — a business outcome, never a transport failure.</summary>
    private sealed class AlwaysDecliningGateway : IPaymentGateway
    {
        public Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken ct)
            => throw new PaymentDeclinedException("insufficient funds");
        public Task<string> RefundAsync(Guid orderId, string? originalGatewayReference, Money amount, CancellationToken ct)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Compensate_mode_saves_a_permanent_Failed_row_unchanged_from_today()
    {
        var options = BaseOptions(GatewayUnavailableMode.Compensate);
        await using var db = NewDb(nameof(Compensate_mode_saves_a_permanent_Failed_row_unchanged_from_today));
        var service = new PaymentService(db, new AlwaysUnavailableGateway(), NewPipeline(options),
            new StaticOptionsMonitor<PaymentOptions>(options), NullLogger<PaymentService>.Instance);
        var orderId = Guid.NewGuid();

        var outcome = await service.ChargeAsync(orderId, new Money(299m, "INR"));

        Assert.Equal(PaymentStatus.Failed, outcome.Status);
        var row = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId);
        Assert.Equal(PaymentStatus.Failed, row.Status);
    }

    [Fact]
    public async Task Buffer_mode_throws_instead_of_saving_Failed_and_leaves_no_payment_row()
    {
        var options = BaseOptions(GatewayUnavailableMode.Buffer);
        await using var db = NewDb(nameof(Buffer_mode_throws_instead_of_saving_Failed_and_leaves_no_payment_row));
        var service = new PaymentService(db, new AlwaysUnavailableGateway(), NewPipeline(options),
            new StaticOptionsMonitor<PaymentOptions>(options), NullLogger<PaymentService>.Instance);
        var orderId = Guid.NewGuid();

        await Assert.ThrowsAsync<GatewayUnavailableRetryLaterException>(() => service.ChargeAsync(orderId, new Money(299m, "INR")));

        // The ghost-row watch-out: no Payment row (Pending or otherwise) must survive the buffered attempt.
        var row = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId);
        Assert.Null(row);
    }

    /// <summary>
    /// The plan's core Buffer-mode assertion: after a Buffer rollback, a SECOND charge attempt for the
    /// SAME order id must actually re-attempt the gateway call from scratch, not short-circuit on a
    /// stale Pending row that the idempotency check at the top of ChargeAsync would otherwise return
    /// forever.
    /// </summary>
    [Fact]
    public async Task Buffer_mode_rollback_then_a_second_attempt_really_calls_the_gateway_again()
    {
        var options = BaseOptions(GatewayUnavailableMode.Buffer);
        await using var db = NewDb(nameof(Buffer_mode_rollback_then_a_second_attempt_really_calls_the_gateway_again));
        var gateway = new RecoveringGateway(failuresBeforeSuccess: 1); // fails once, then the "gateway recovered"
        var service = new PaymentService(db, gateway, NewPipeline(options),
            new StaticOptionsMonitor<PaymentOptions>(options), NullLogger<PaymentService>.Instance);
        var orderId = Guid.NewGuid();

        // First attempt: gateway unavailable, buffered (thrown), row deleted.
        await Assert.ThrowsAsync<GatewayUnavailableRetryLaterException>(() => service.ChargeAsync(orderId, new Money(299m, "INR")));
        Assert.Equal(1, gateway.CallCount);
        Assert.Null(await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId));

        // Second attempt (simulating the consumer's redelivery after seeking back): must call the
        // gateway a SECOND time, not read back a stale Pending row and short-circuit.
        var outcome = await service.ChargeAsync(orderId, new Money(299m, "INR"));

        Assert.Equal(2, gateway.CallCount);
        Assert.Equal(PaymentStatus.Completed, outcome.Status);
        Assert.Equal("REF-2", outcome.GatewayReference);
    }

    [Fact]
    public async Task A_business_decline_is_always_Compensate_even_in_Buffer_mode()
    {
        var options = BaseOptions(GatewayUnavailableMode.Buffer); // Buffer is set, but a decline is not a transport failure
        await using var db = NewDb(nameof(A_business_decline_is_always_Compensate_even_in_Buffer_mode));
        var service = new PaymentService(db, new AlwaysDecliningGateway(), NewPipeline(options),
            new StaticOptionsMonitor<PaymentOptions>(options), NullLogger<PaymentService>.Instance);
        var orderId = Guid.NewGuid();

        var outcome = await service.ChargeAsync(orderId, new Money(299m, "INR"));

        Assert.Equal(PaymentStatus.Failed, outcome.Status); // returned normally, not thrown
        var row = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId);
        Assert.Equal(PaymentStatus.Failed, row.Status); // a decline still persists a permanent Failed row
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
