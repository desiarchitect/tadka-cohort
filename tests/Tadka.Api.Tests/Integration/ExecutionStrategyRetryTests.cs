using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Controllers;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>Same real-Postgres factory, but with the Day-14 cloud failover flag forced ON (ADR-064).</summary>
public class RetryingTadkaApiFactory : TadkaApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:EnableRetryOnFailure", "true");
    }
}

/// <summary>
/// ADR-064: with <c>Database:EnableRetryOnFailure=true</c>, EF Core's retrying execution strategy refuses a
/// user-opened transaction unless the whole unit runs inside <c>CreateExecutionStrategy().ExecuteAsync</c>.
/// These tests prove (1) the flag really installs the retrying strategy, (2) the unwrapped pattern throws,
/// which is the gotcha the Day-14 failover demo surfaces, and (3) a wrapped path works under the flag.
/// </summary>
public class ExecutionStrategyRetryTests(RetryingTadkaApiFactory factory) : IClassFixture<RetryingTadkaApiFactory>
{
    private readonly RetryingTadkaApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public void Flag_on_installs_a_retrying_execution_strategy()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var readDb = scope.ServiceProvider.GetRequiredService<TadkaReadDbContext>();

        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.True(readDb.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [Fact]
    public async Task Flag_on_rejects_an_unwrapped_user_transaction()
    {
        // The exact bug the wrap fixes: BeginTransaction + a query OUTSIDE the strategy.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Coupons.CountAsync();
        });
        Assert.Contains("user-initiated transactions", ex.Message);
    }

    [Fact]
    public async Task Flag_on_wrapped_pessimistic_redeem_still_serializes_correctly()
    {
        // CouponsController.RedeemPessimistic = BeginTransaction + SELECT ... FOR UPDATE, now inside
        // CreateExecutionStrategy().ExecuteAsync. Unwrapped, every call here would be a 500.
        var code = await SeedCouponAsync(maxRedemptions: 100);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _client.PostAsJsonAsync($"/api/v1/coupons/{code}/redeem/pessimistic", new RedeemRequest(Guid.NewGuid()))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var coupon = await db.Coupons.AsNoTracking().FirstAsync(c => c.Code == code);
        var rows = await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.CouponId == coupon.Id);
        Assert.Equal(10, coupon.Redeemed);
        Assert.Equal(10, rows);
    }

    private async Task<string> SeedCouponAsync(int maxRedemptions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var code = $"RTRY{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        db.Coupons.Add(new Coupon { Id = Guid.NewGuid(), Code = code, MaxRedemptions = maxRedemptions, Redeemed = 0 });
        await db.SaveChangesAsync();
        return code;
    }
}
