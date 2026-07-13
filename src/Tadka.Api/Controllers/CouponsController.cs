using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Exceptions;

namespace Tadka.Api.Controllers;

/// <summary>
/// Day 4 break kit, Demo 5 (ADR-045): a flash-sale-shaped write - many customers race to
/// claim a scarce resource (100 TADKA50 coupons). Optimistic concurrency (ADR-012) is the
/// RIGHT default for orders, where two customers rarely touch the SAME row. Here every
/// request hits the SAME coupon row, so optimistic locking degrades into a 409 retry storm
/// instead of preventing the problem. The `strategy` route segment lets the break-kit compare
/// all three approaches against the identical concurrent-load script.
/// </summary>
[ApiController]
[Route("api/v1/coupons")]
public class CouponsController(TadkaDbContext db) : ControllerBase
{
    private readonly TadkaDbContext _db = db;

    [HttpGet("{code}")]
    public async Task<ActionResult<CouponResponse>> GetByCode(string code)
    {
        var coupon = await _db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code);
        if (coupon is null) throw new NotFoundException(nameof(Coupon), code);
        return Ok(new CouponResponse(coupon.Code, coupon.MaxRedemptions, coupon.Redeemed, coupon.Remaining));
    }

    // BROKEN on purpose: read-then-write with NO concurrency guard at all. Under concurrent
    // load, two requests both read Redeemed=63, both compute 64 in memory, both write 64 - one
    // increment is lost. Worse, both requests still insert a CouponRedemption row, so COUNT(*)
    // can exceed MaxRedemptions (oversell) even while the Redeemed counter under-counts. This is
    // the "it worked in my single-user test" version every team ships once.
    //
    // AsNoTracking() is deliberate: Coupon has xmin configured as a concurrency token (for the
    // optimistic/pessimistic endpoints below), and EF enforces that token on every tracked
    // SaveChanges - so a tracked read-modify-write here would accidentally get the fix for free.
    // Reading untracked and writing via a plain UPDATE (no version predicate) is what "no guard
    // at all" actually looks like.
    [HttpPost("{code}/redeem/none")]
    public async Task<ActionResult<RedeemResponse>> RedeemNone(string code, [FromBody] RedeemRequest request)
    {
        var coupon = await _db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code);
        if (coupon is null) throw new NotFoundException(nameof(Coupon), code);

        if (coupon.IsExhausted)
            return Problem(detail: $"Coupon '{code}' is exhausted.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Coupon Exhausted");

        // The time-of-check-to-time-of-use gap: newRedeemed is computed from the value we read,
        // then blindly written back - whichever request writes last wins, silently discarding
        // any increment another concurrent request made in between.
        var newRedeemed = coupon.Redeemed + 1;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ordering.coupons SET \"Redeemed\" = {newRedeemed} WHERE \"Id\" = {coupon.Id}");
        _db.CouponRedemptions.Add(new CouponRedemption { Id = Guid.NewGuid(), CouponId = coupon.Id, CustomerId = request.CustomerId, RedeemedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        return Ok(new RedeemResponse(coupon.Code, newRedeemed, coupon.MaxRedemptions - newRedeemed, "none"));
    }

    // FIX #1: optimistic concurrency (same xmin pattern as Orders, ADR-012). Correct - the DB
    // rejects a write against a stale row - but on ONE hot row under load, every loser gets a
    // 409 and must retry, so throughput collapses into a retry storm as contention rises.
    [HttpPost("{code}/redeem/optimistic")]
    public async Task<ActionResult<RedeemResponse>> RedeemOptimistic(string code, [FromBody] RedeemRequest request)
    {
        var coupon = await _db.Coupons.FirstOrDefaultAsync(c => c.Code == code);
        if (coupon is null) throw new NotFoundException(nameof(Coupon), code);

        if (coupon.IsExhausted)
            return Problem(detail: $"Coupon '{code}' is exhausted.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Coupon Exhausted");

        coupon.Redeemed += 1;
        _db.CouponRedemptions.Add(new CouponRedemption { Id = Guid.NewGuid(), CouponId = coupon.Id, CustomerId = request.CustomerId, RedeemedAt = DateTime.UtcNow });

        // DbUpdateConcurrencyException (xmin mismatch under a concurrent redeem) -> 409 via the
        // shared exception middleware - correct, but the CALLER now owns a retry loop.
        await _db.SaveChangesAsync();

        return Ok(new RedeemResponse(coupon.Code, coupon.Redeemed, coupon.Remaining, "optimistic"));
    }

    // FIX #2 (decided, ADR-045): pessimistic locking. `SELECT ... FOR UPDATE` on the coupon row
    // serializes redemptions at the database - every other concurrent request queues instead of
    // racing or bouncing off a 409. Correct AND no retry storm, at the cost of holding a row
    // lock for the duration of the transaction (bad if that transaction later does something
    // slow - see the Demo 6 lock-hold beat below for exactly why that matters).
    [HttpPost("{code}/redeem/pessimistic")]
    public async Task<ActionResult<RedeemResponse>> RedeemPessimistic(string code, [FromBody] RedeemRequest request)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        // xmin is a Postgres system column and is NOT included by `SELECT *` - it must be
        // named explicitly or EF's materializer throws (the entity has it mapped as a shadow
        // concurrency property, copied from the Orders convention, ADR-012). Column names are
        // quoted PascalCase (EF's default Npgsql convention, same as every other table here) -
        // an unquoted `code` resolves to Postgres's lower-cased identifier and does not match.
        var coupon = await _db.Coupons
            .FromSqlInterpolated($"SELECT *, xmin FROM ordering.coupons WHERE \"Code\" = {code} FOR UPDATE")
            .FirstOrDefaultAsync();
        if (coupon is null) throw new NotFoundException(nameof(Coupon), code);

        if (coupon.IsExhausted)
        {
            await tx.RollbackAsync();
            return Problem(detail: $"Coupon '{code}' is exhausted.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Coupon Exhausted");
        }

        coupon.Redeemed += 1;
        _db.CouponRedemptions.Add(new CouponRedemption { Id = Guid.NewGuid(), CouponId = coupon.Id, CustomerId = request.CustomerId, RedeemedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new RedeemResponse(coupon.Code, coupon.Redeemed, coupon.Remaining, "pessimistic"));
    }

    // Demo-only reset so the break kit can be re-run without a fresh database.
    [HttpPost("{code}/reset")]
    public async Task<ActionResult> Reset(string code)
    {
        var coupon = await _db.Coupons.FirstOrDefaultAsync(c => c.Code == code);
        if (coupon is null) throw new NotFoundException(nameof(Coupon), code);

        coupon.Redeemed = 0;
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ordering.coupon_redemptions WHERE \"CouponId\" = {coupon.Id}");
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CouponResponse(string Code, int MaxRedemptions, int Redeemed, int Remaining);

public record RedeemRequest(Guid CustomerId);

public record RedeemResponse(string Code, int Redeemed, int Remaining, string Strategy);
