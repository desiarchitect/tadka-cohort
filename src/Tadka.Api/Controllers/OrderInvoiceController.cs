using Microsoft.AspNetCore.Mvc;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Exceptions;
using Tadka.Api.Infrastructure.Security;

namespace Tadka.Api.Controllers;

/// <summary>
/// Day 6, Beat (CDN emulation - signed URLs, ADR-050): a time-limited link to a resource that
/// doesn't otherwise require auth on this branch (Day 10 adds real JWT auth for order ownership -
/// this predates it and stands on its own, the same mechanic an S3 presigned URL or a CDN signed
/// cookie uses). No PDF generation - the receipt is JSON; the point being taught is the signing
/// mechanic (expiry + tamper-evidence), not document rendering.
/// </summary>
[ApiController]
[Route("api/v1/orders/{id:guid}/invoice")]
public class OrderInvoiceController(IOrderRepository orders, UrlSigner signer) : ControllerBase
{
    private readonly IOrderRepository _orders = orders;
    private readonly UrlSigner _signer = signer;
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromMinutes(5);

    /// <summary>Issues a signed, time-limited invoice URL for this order.</summary>
    [HttpPost("sign")]
    public async Task<ActionResult<SignedInvoiceUrlResponse>> Sign(Guid id)
    {
        var order = await _orders.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Domain.Orders.Order), id);

        var (signature, expiresAt) = _signer.Sign(id.ToString(), DefaultValidity);
        var url = Url.Action(nameof(Get), "OrderInvoice", new { id, sig = signature, exp = expiresAt }, Request.Scheme)!;
        return Ok(new SignedInvoiceUrlResponse(url, DateTimeOffset.FromUnixTimeSeconds(expiresAt)));
    }

    /// <summary>Fetches the invoice — requires a valid, unexpired signature (no session/JWT check).</summary>
    [HttpGet]
    public async Task<ActionResult<InvoiceResponse>> Get(Guid id, [FromQuery] string? sig, [FromQuery] long exp)
    {
        if (string.IsNullOrEmpty(sig))
            return Problem(detail: "Missing signature.", statusCode: StatusCodes.Status401Unauthorized, title: "Signature Required");

        if (!_signer.Verify(id.ToString(), exp, sig))
            return Problem(detail: "Invalid or expired signature.", statusCode: StatusCodes.Status403Forbidden, title: "Signature Invalid");

        var order = await _orders.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Domain.Orders.Order), id);

        return Ok(new InvoiceResponse(
            order.Id, order.CustomerId, order.TotalAmount.Amount, order.TotalAmount.Currency,
            order.Status.ToString(), order.CreatedAt));
    }
}

public record SignedInvoiceUrlResponse(string Url, DateTimeOffset ExpiresAt);

public record InvoiceResponse(Guid OrderId, Guid CustomerId, decimal Amount, string Currency, string Status, DateTime CreatedAt);
