using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Auth;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;
using Tadka.Api.Infrastructure.Pii;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController(TadkaDbContext db, ILogger<UsersController> logger) : ControllerBase
{
    /// <summary>The caller's own profile — PII returned to the owner; masked for anyone else (ADR-031/032).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult> Get(Guid id)
    {
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        var isSelfOrAdmin = User.IsAdmin() || User.UserId() == id;
        return Ok(new
        {
            user.Id,
            user.Name,
            user.Role,
            Email = isSelfOrAdmin ? user.Email : PiiMasker.Email(user.Email),
            Phone = isSelfOrAdmin ? user.Phone : PiiMasker.Phone(user.Phone)
        });
    }

    /// <summary>
    /// GDPR right-to-be-forgotten (ADR-032): ANONYMISE, don't hard-delete (order history must still
    /// reconcile). Self or Admin only. Honest limit: events already emitted to Kafka/outbox are not
    /// retro-scrubbed — we minimise PII in events + crypto-shred for the rest.
    /// </summary>
    [HttpPost("{id:guid}/forget")]
    public async Task<ActionResult> Forget(Guid id)
    {
        if (!User.IsAdmin() && User.UserId() != id) return Forbid();

        var user = await db.Set<User>().Include(u => u.SavedAddresses).FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        user.Name = "[deleted]";
        user.Email = $"deleted+{id:N}@tadka.invalid";
        user.Phone = "";
        user.PasswordHash = "";
        user.SavedAddresses.Clear();
        await db.SaveChangesAsync();

        logger.LogInformation(
            "User {UserId} anonymised (RTBF). Events already on Kafka/outbox are NOT retro-scrubbed (ADR-032).", id);
        return NoContent();
    }
}
