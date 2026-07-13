using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Controllers;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Day 4 break kit, Demo 5 (ADR-045): the same hot-row access pattern tested against all three
/// redemption strategies, proving the numbers the break kit captures - not just asserting them.
/// Each test seeds its OWN coupon (unique code) so tests never interfere with each other or with
/// the shared TADKA50 seed used by the manual class demo.
/// </summary>
public class CouponConcurrencyIntegrationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task RedeemNone_two_stale_reads_produce_a_lost_update()
    {
        // Deterministic reproduction (same style as OrderFlowIntegrationTests'
        // xmin_concurrency_token_rejects_a_stale_write): two DbContexts read the SAME stale
        // Redeemed value, both compute value+1, both write. The second write clobbers the first.
        var code = await SeedCouponAsync(maxRedemptions: 10);

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<TadkaDbContext>();

        var couponA = await dbA.Coupons.AsNoTracking().FirstAsync(c => c.Code == code); // Redeemed = 0
        var couponB = await dbB.Coupons.AsNoTracking().FirstAsync(c => c.Code == code); // SAME stale Redeemed = 0

        // Mirrors CouponsController.RedeemNone exactly: no version check on the write.
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ordering.coupons SET \"Redeemed\" = {couponA.Redeemed + 1} WHERE \"Id\" = {couponA.Id}");
        await dbB.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ordering.coupons SET \"Redeemed\" = {couponB.Redeemed + 1} WHERE \"Id\" = {couponB.Id}");

        var final = await dbA.Coupons.AsNoTracking().FirstAsync(c => c.Code == code);
        // Two redemptions happened, but B's blind overwrite lost A's increment: counter is 1, not 2.
        Assert.Equal(1, final.Redeemed);
    }

    [Fact]
    public async Task RedeemOptimistic_concurrent_requests_stay_consistent_but_conflict()
    {
        var code = await SeedCouponAsync(maxRedemptions: 100);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => RedeemAsync(code, "optimistic")));

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(20, okCount + conflictCount); // every request resolves as one or the other
        Assert.True(conflictCount > 0, "expected at least one 409 retry under 20-way contention on one row");

        var (counter, rows) = await ReadCouponStateAsync(code);
        Assert.Equal(okCount, counter); // no lost updates - just fewer winners
        Assert.Equal(okCount, rows);
    }

    [Fact]
    public async Task RedeemPessimistic_concurrent_requests_all_succeed_with_no_conflicts()
    {
        var code = await SeedCouponAsync(maxRedemptions: 100);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => RedeemAsync(code, "pessimistic")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode)); // zero 409s

        var (counter, rows) = await ReadCouponStateAsync(code);
        Assert.Equal(20, counter);
        Assert.Equal(20, rows);
    }

    [Fact]
    public async Task RedeemPessimistic_never_oversells_past_the_cap()
    {
        var code = await SeedCouponAsync(maxRedemptions: 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => RedeemAsync(code, "pessimistic")));

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var exhaustedCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(10, okCount); // exactly the cap, never more
        Assert.Equal(15, exhaustedCount);

        var (counter, rows) = await ReadCouponStateAsync(code);
        Assert.Equal(10, counter);
        Assert.Equal(10, rows);
    }

    // --- helpers -------------------------------------------------------------

    private Task<HttpResponseMessage> RedeemAsync(string code, string strategy) =>
        _client.PostAsJsonAsync($"/api/v1/coupons/{code}/redeem/{strategy}", new RedeemRequest(Guid.NewGuid()));

    private async Task<string> SeedCouponAsync(int maxRedemptions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var code = $"TEST{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        db.Coupons.Add(new Coupon { Id = Guid.NewGuid(), Code = code, MaxRedemptions = maxRedemptions, Redeemed = 0 });
        await db.SaveChangesAsync();
        return code;
    }

    private async Task<(int Counter, int Rows)> ReadCouponStateAsync(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var coupon = await db.Coupons.AsNoTracking().FirstAsync(c => c.Code == code);
        var rows = await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.CouponId == coupon.Id);
        return (coupon.Redeemed, rows);
    }
}
