using Microsoft.EntityFrameworkCore;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Repositories;

public class OrderRepository(TadkaDbContext db) : IOrderRepository
{
    private readonly TadkaDbContext _db = db;

    public async Task<Order?> GetByIdAsync(Guid id)
    {
        return await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);
    }

    public async Task<List<Order>> GetByCustomerIdAsync(Guid customerId, int page, int pageSize)
    {
        return await _db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> CountByCustomerIdAsync(Guid customerId)
    {
        return await _db.Orders
            .Where(o => o.CustomerId == customerId)
            .CountAsync();
    }

    public async Task<List<Order>> GetAllAsync(int page, int pageSize)
    {
        return await _db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> CountAllAsync()
    {
        return await _db.Orders.CountAsync();
    }

    public void Add(Order order)
    {
        _db.Orders.Add(order);
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
