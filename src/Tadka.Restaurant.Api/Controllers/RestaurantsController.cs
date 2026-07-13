using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Restaurant.Api.Caching;
using Tadka.Restaurant.Api.Contracts;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Domain;
using Tadka.Restaurant.Api.Filters;
using Tadka.Restaurant.Api.Messaging;

namespace Tadka.Restaurant.Api.Controllers;

/// <summary>
/// The Restaurant service's API (ADR-036) — moved from the monolith (a move, not a rewrite). Reads are
/// cache-aside over Redis (ADR-018/019). Every mutation that changes pricing/availability stages a
/// <c>menu-updated</c> snapshot on the Outbox in the SAME transaction (ADR-028/037) so the monolith's
/// local price replica stays fresh without a back-call (ADR-008).
/// </summary>
[ApiController]
[Route("api/v1/restaurants")]
public class RestaurantsController(RestaurantDbContext db, ICacheService cache) : ControllerBase
{
    private static readonly TimeSpan MenuTtl = TimeSpan.FromSeconds(60);
    private static string MenuCacheKey(Guid restaurantId) => $"restaurant:{restaurantId}:menu";

    // Resource-ownership (ADR-031): an owner may only touch THEIR restaurant; Admin may touch any.
    private bool OwnsOrAdmin(Guid restaurantId) => User.IsAdmin() || User.OwnedRestaurantId() == restaurantId;

    [HttpGet]
    [ETagFilter] // ADR-048/054: conditional GET — 304 when body unchanged
    public async Task<ActionResult<PagedResponse<RestaurantResponse>>> GetAll(
        [FromQuery] string? city, [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);

        var query = db.Restaurants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(r => r.Address.City.ToLower() == city.ToLower());

        var totalCount = await query.CountAsync();
        var items = await query.OrderBy(r => r.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => Map(r)).ToListAsync();
        return Ok(new PagedResponse<RestaurantResponse>(items, page, pageSize, totalCount));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RestaurantResponse>> GetById(Guid id)
    {
        var r = await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return r is null ? NotFound() : Ok(Map(r));
    }

    [HttpGet("{id:guid}/menu")]
    [ETagFilter] // ADR-048/054: conditional GET — 304 when body unchanged
    public async Task<ActionResult<List<MenuItemResponse>>> GetMenu(
        Guid id, [FromQuery] string? category, [FromQuery] bool? vegOnly)
    {
        // Cache-aside (ADR-018): the full menu is a hot, rarely-changing read; cache the whole list and
        // filter in-memory so one cached entry serves every filter combo (stampede-protected, ADR-019).
        var allItems = await cache.GetOrSetAsync(
            MenuCacheKey(id),
            async () =>
            {
                var r = await db.Restaurants.AsNoTracking().Include(x => x.Menu).FirstOrDefaultAsync(x => x.Id == id);
                return r?.Menu.Select(MapItem).ToList();
            },
            MenuTtl);

        if (allItems is null) return NotFound();

        IEnumerable<MenuItemResponse> items = allItems;
        if (!string.IsNullOrWhiteSpace(category))
            items = items.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (vegOnly == true) items = items.Where(i => i.IsVeg);
        return Ok(items.ToList());
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<ActionResult<RestaurantResponse>> Create(
        [FromBody] CreateRestaurantRequest request, [FromServices] IValidator<CreateRestaurantRequest> validator)
    {
        var result = await validator.ValidateAsync(request);
        if (!result.IsValid) return ValidationProblem(ToModelState(result));

        var restaurant = new Domain.Restaurant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = new Address(request.Address.Line1, request.Address.Line2, request.Address.City,
                request.Address.Pincode, request.Address.Latitude, request.Address.Longitude),
            IsActive = true,
            AvgPrepTimeMinutes = request.AvgPrepTimeMinutes,
            CreatedAt = DateTime.UtcNow
        };
        db.Restaurants.Add(restaurant);
        StageSnapshot(restaurant);          // menu-updated (empty menu) — replica learns the restaurant exists
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = restaurant.Id }, Map(restaurant));
    }

    [Authorize(Roles = "RestaurantOwner,Admin")]
    [HttpPost("{id:guid}/menu")]
    public async Task<ActionResult<MenuItemResponse>> AddMenuItem(
        Guid id, [FromBody] CreateMenuItemRequest request, [FromServices] IValidator<CreateMenuItemRequest> validator)
    {
        if (!OwnsOrAdmin(id)) return Forbid();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var restaurant = await db.Restaurants.Include(r => r.Menu).FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null) return NotFound();

        var item = new MenuItem
        {
            Id = Guid.NewGuid(), // app-generated → known before save → can stage the snapshot in the same txn
            Name = request.Name,
            Description = request.Description,
            Price = new Money(request.Price.Amount, request.Price.Currency),
            Category = request.Category,
            IsAvailable = true,
            IsVeg = request.IsVeg
        };
        restaurant.Menu.Add(item);
        StageSnapshot(restaurant);
        await db.SaveChangesAsync();
        await cache.RemoveAsync(MenuCacheKey(id)); // delete-on-write (ADR-018)
        return CreatedAtAction(nameof(GetMenu), new { id = restaurant.Id }, MapItem(item));
    }

    // PATCH a menu item — e.g. a restaurant raising a dish's price (the price-propagation demo).
    [Authorize(Roles = "RestaurantOwner,Admin")]
    [HttpPatch("{id:guid}/menu/{itemId:guid}")]
    public async Task<ActionResult> UpdateMenuItem(Guid id, Guid itemId, [FromBody] UpdateMenuItemRequest request)
    {
        if (!OwnsOrAdmin(id)) return Forbid();
        var restaurant = await db.Restaurants.Include(r => r.Menu).FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null) return NotFound();
        var item = restaurant.Menu.FirstOrDefault(m => m.Id == itemId);
        if (item is null) return NotFound();

        if (request.Name is not null) item.Name = request.Name;
        if (request.Description is not null) item.Description = request.Description;
        if (request.Price is not null) item.Price = new Money(request.Price.Amount, request.Price.Currency);
        if (request.Category is not null) item.Category = request.Category;
        if (request.IsVeg.HasValue) item.IsVeg = request.IsVeg.Value;
        if (request.IsAvailable.HasValue) item.IsAvailable = request.IsAvailable.Value;

        StageSnapshot(restaurant);
        await db.SaveChangesAsync();
        await cache.RemoveAsync(MenuCacheKey(id));
        return NoContent();
    }

    [Authorize(Roles = "RestaurantOwner,Admin")]
    [HttpPatch("{id:guid}/menu/{itemId:guid}/availability")]
    public async Task<ActionResult> UpdateMenuItemAvailability(Guid id, Guid itemId, [FromBody] UpdateAvailabilityRequest request)
    {
        if (!OwnsOrAdmin(id)) return Forbid();
        var restaurant = await db.Restaurants.Include(r => r.Menu).FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null) return NotFound();
        var item = restaurant.Menu.FirstOrDefault(m => m.Id == itemId);
        if (item is null) return NotFound();

        item.IsAvailable = request.IsAvailable;
        StageSnapshot(restaurant);
        await db.SaveChangesAsync();
        await cache.RemoveAsync(MenuCacheKey(id));
        return NoContent();
    }

    [Authorize(Roles = "RestaurantOwner,Admin")]
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult> UpdateRestaurant(Guid id, [FromBody] UpdateRestaurantRequest request)
    {
        if (!OwnsOrAdmin(id)) return Forbid();
        var restaurant = await db.Restaurants.Include(r => r.Menu).FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null) return NotFound();

        if (request.Name is not null) restaurant.Name = request.Name;
        if (request.AvgPrepTimeMinutes.HasValue) restaurant.AvgPrepTimeMinutes = request.AvgPrepTimeMinutes.Value;
        if (request.IsActive.HasValue) restaurant.IsActive = request.IsActive.Value;

        StageSnapshot(restaurant);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Stage a full <c>menu-updated</c> snapshot on the Outbox (ADR-037, event-carried state
    /// transfer). Written in the SAME SaveChanges as the change — no dual-write (ADR-028).</summary>
    private void StageSnapshot(Domain.Restaurant r)
    {
        var snapshot = new RestaurantSnapshotMessage(
            Guid.NewGuid(), r.Id, r.Name, r.IsActive,
            new AddressSnapshot(r.Address.Line1, r.Address.Line2, r.Address.City, r.Address.Pincode, r.Address.Latitude, r.Address.Longitude),
            r.Menu.Select(m => new MenuItemSnapshot(m.Id, m.Name, m.Price.Amount, m.Price.Currency, m.IsAvailable, m.Category, m.IsVeg)).ToList());

        db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = Topics.MenuUpdated,
            Key = r.Id.ToString(),
            Payload = JsonSerializer.Serialize(snapshot),
            TraceParent = Tadka.Telemetry.TadkaTrace.CurrentTraceParent()   // carry the trace across Kafka (ADR-041)
        });
    }

    private static RestaurantResponse Map(Domain.Restaurant r) => new(
        r.Id, r.Name,
        new AddressResponse(r.Address.Line1, r.Address.Line2, r.Address.City, r.Address.Pincode, r.Address.Latitude, r.Address.Longitude),
        r.IsActive, r.AvgPrepTimeMinutes, r.CreatedAt);

    private static MenuItemResponse MapItem(MenuItem m) => new(
        m.Id, m.Name, m.Description, new MoneyResponse(m.Price.Amount, m.Price.Currency), m.Category, m.IsAvailable, m.IsVeg);

    private static Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary ToModelState(FluentValidation.Results.ValidationResult result)
    {
        var ms = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        foreach (var e in result.Errors) ms.AddModelError(e.PropertyName, e.ErrorMessage);
        return ms;
    }
}
