using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Contracts;
using Tadka.Api.Contracts.Restaurants;
using Tadka.Api.Data;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Exceptions;
using Tadka.Api.Infrastructure.Caching;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/restaurants")]
public class RestaurantsController : ControllerBase
{
    private readonly TadkaDbContext _db;        // primary — writes
    private readonly TadkaReadDbContext _read;  // replica — read-heavy GETs (ADR-016)
    private readonly ICacheService _cache;      // Redis menu cache (ADR-018/019)
    private static readonly TimeSpan MenuTtl = TimeSpan.FromSeconds(60);

    public RestaurantsController(TadkaDbContext db, TadkaReadDbContext read, ICacheService cache)
    {
        _db = db;
        _read = read;
        _cache = cache;
    }

    private static string MenuCacheKey(Guid restaurantId) => $"restaurant:{restaurantId}:menu";

    [HttpGet]
    public async Task<ActionResult<PagedResponse<RestaurantResponse>>> GetAll(
        [FromQuery] string? city,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);

        var query = _read.Restaurants.AsQueryable(); // replica read

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(r => r.Address.City.ToLower() == city.ToLower());

        var totalCount = await query.CountAsync();

        var restaurants = await query
            .OrderBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => MapToResponse(r))
            .ToListAsync();

        return Ok(new PagedResponse<RestaurantResponse>(restaurants, page, pageSize, totalCount));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RestaurantResponse>> GetById(Guid id)
    {
        var restaurant = await _read.Restaurants
            .FirstOrDefaultAsync(r => r.Id == id);

        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), id);

        return Ok(MapToResponse(restaurant));
    }

    [HttpPost]
    public async Task<ActionResult<RestaurantResponse>> Create(
        [FromBody] CreateRestaurantRequest request,
        [FromServices] IValidator<CreateRestaurantRequest> validator)
    {
        var result = await validator.ValidateAsync(request);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);

        var restaurant = new Restaurant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = new Address(
                request.Address.Line1,
                request.Address.Line2,
                request.Address.City,
                request.Address.Pincode,
                request.Address.Latitude,
                request.Address.Longitude),
            IsActive = true,
            AvgPrepTimeMinutes = request.AvgPrepTimeMinutes,
            CreatedAt = DateTime.UtcNow
        };

        _db.Restaurants.Add(restaurant);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = restaurant.Id }, MapToResponse(restaurant));
    }

    [HttpGet("{id:guid}/menu")]
    public async Task<ActionResult<List<MenuItemResponse>>> GetMenu(
        Guid id,
        [FromQuery] string? category,
        [FromQuery] bool? vegOnly)
    {
        // Cache-aside (ADR-018): the full menu is a hot, rarely-changing read. Cache the whole list
        // (replica-backed on a miss), then apply category/vegOnly filters in-memory so one cached
        // entry serves every filter combo. Stampede-protected by a single-flight lock (ADR-019).
        var allItems = await _cache.GetOrSetAsync(
            MenuCacheKey(id),
            async () =>
            {
                var restaurant = await _read.Restaurants
                    .Include(r => r.Menu)
                    .FirstOrDefaultAsync(r => r.Id == id);
                return restaurant?.Menu.Select(MapMenuItemToResponse).ToList();
            },
            MenuTtl);

        if (allItems is null)
            throw new NotFoundException(nameof(Restaurant), id);

        IEnumerable<MenuItemResponse> items = allItems;

        if (!string.IsNullOrWhiteSpace(category))
            items = items.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (vegOnly == true)
            items = items.Where(i => i.IsVeg);

        return Ok(items.ToList());
    }

    [HttpPatch("{id:guid}/menu/{itemId:guid}/availability")]
    public async Task<ActionResult> UpdateMenuItemAvailability(
        Guid id,
        Guid itemId,
        [FromBody] UpdateAvailabilityRequest request)
    {
        var restaurant = await _db.Restaurants
            .Include(r => r.Menu)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), id);

        var menuItem = restaurant.Menu.FirstOrDefault(m => m.Id == itemId);
        if (menuItem is null)
            throw new NotFoundException(nameof(MenuItem), itemId);

        menuItem.IsAvailable = request.IsAvailable;
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync(MenuCacheKey(id)); // delete-on-write (ADR-018)

        return NoContent();
    }

    // PATCH a restaurant: partial update + deactivate (our "delete" — no hard DELETE).
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult> UpdateRestaurant(
        Guid id,
        [FromBody] UpdateRestaurantRequest request)
    {
        var restaurant = await _db.Restaurants.FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), id);

        if (request.Name is not null) restaurant.Name = request.Name;
        if (request.AvgPrepTimeMinutes.HasValue) restaurant.AvgPrepTimeMinutes = request.AvgPrepTimeMinutes.Value;
        if (request.IsActive.HasValue) restaurant.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // POST a new menu item (restaurant-partner flow). A menu item is created within its restaurant.
    [HttpPost("{id:guid}/menu")]
    public async Task<ActionResult<MenuItemResponse>> AddMenuItem(
        Guid id,
        [FromBody] CreateMenuItemRequest request,
        [FromServices] IValidator<CreateMenuItemRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        var restaurant = await _db.Restaurants
            .Include(r => r.Menu)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), id);

        var item = new MenuItem
        {
            // Id is store-generated (gen_random_uuid). Leave it unset so EF marks this
            // as an INSERT when added to the already-tracked restaurant's Menu collection
            // — setting a non-empty key makes EF think it's an existing row (→ UPDATE → 500).
            Name = request.Name,
            Description = request.Description,
            Price = new Money(request.Price.Amount, request.Price.Currency),
            Category = request.Category,
            IsAvailable = true,
            IsVeg = request.IsVeg
        };
        restaurant.Menu.Add(item);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync(MenuCacheKey(id)); // delete-on-write (ADR-018)

        return CreatedAtAction(nameof(GetMenu), new { id = restaurant.Id }, MapMenuItemToResponse(item));
    }

    // PATCH a menu item: partial update — e.g. a restaurant raising a dish's price.
    [HttpPatch("{id:guid}/menu/{itemId:guid}")]
    public async Task<ActionResult> UpdateMenuItem(
        Guid id,
        Guid itemId,
        [FromBody] UpdateMenuItemRequest request)
    {
        var restaurant = await _db.Restaurants
            .Include(r => r.Menu)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), id);

        var item = restaurant.Menu.FirstOrDefault(m => m.Id == itemId);
        if (item is null)
            throw new NotFoundException(nameof(MenuItem), itemId);

        if (request.Name is not null) item.Name = request.Name;
        if (request.Description is not null) item.Description = request.Description;
        if (request.Price is not null) item.Price = new Money(request.Price.Amount, request.Price.Currency);
        if (request.Category is not null) item.Category = request.Category;
        if (request.IsVeg.HasValue) item.IsVeg = request.IsVeg.Value;
        if (request.IsAvailable.HasValue) item.IsAvailable = request.IsAvailable.Value;

        await _db.SaveChangesAsync();
        await _cache.RemoveAsync(MenuCacheKey(id)); // delete-on-write (ADR-018)
        return NoContent();
    }

    private static RestaurantResponse MapToResponse(Restaurant r) => new(
        r.Id,
        r.Name,
        new RestaurantAddressResponse(
            r.Address.Line1,
            r.Address.Line2,
            r.Address.City,
            r.Address.Pincode,
            r.Address.Latitude,
            r.Address.Longitude),
        r.IsActive,
        r.AvgPrepTimeMinutes,
        r.CreatedAt);

    private static MenuItemResponse MapMenuItemToResponse(MenuItem m) => new(
        m.Id,
        m.Name,
        m.Description,
        new MoneyResponse(m.Price.Amount, m.Price.Currency),
        m.Category,
        m.IsAvailable,
        m.IsVeg);
}
