using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Tadka.Api.Filters;

/// <summary>
/// Day 6, Beat (ADR-048): conditional GET for cacheable reads. Hashes the response body and
/// compares against the client's If-None-Match; on a match, swaps in a 304 with an EMPTY body
/// instead of re-sending the full JSON — the whole point being that a client that already has
/// the current menu doesn't re-download it. Apply only to reads that are safe to cache
/// (menu/restaurant GETs) — never on writes or on anything customer-specific like an order.
/// </summary>
public sealed class ETagFilterAttribute : ActionFilterAttribute
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var resultContext = await next();

        if (resultContext.Result is not ObjectResult { Value: not null } objectResult)
            return;
        // Check the ObjectResult's OWN declared status, not HttpContext.Response.StatusCode —
        // at this point in the pipeline the result hasn't been executed onto the response yet,
        // so the response's status is still the unset default (200) regardless of what this
        // ObjectResult actually is (e.g. a NotFound(...) or Conflict(...) would misread as success).
        var statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
        if (statusCode is < 200 or >= 300)
            return;

        var body = JsonSerializer.SerializeToUtf8Bytes(objectResult.Value, objectResult.Value.GetType(), Json);
        var hash = Convert.ToHexString(SHA256.HashData(body));
        var etag = $"\"{hash}\"";

        var response = resultContext.HttpContext.Response;
        response.Headers.ETag = etag;
        // "public" (not "private"): a restaurant/menu listing isn't personalized, so a SHARED
        // cache (a proxy, the edge cache in front of this LB) is allowed to store it too - per
        // HTTP semantics, "private" means "shared caches must not store this," which is why the
        // edge cache (ADR-050) silently refused to cache these responses until this was fixed.
        response.Headers.CacheControl = "public, must-revalidate";

        var ifNoneMatch = resultContext.HttpContext.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
            resultContext.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
    }
}
