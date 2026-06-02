using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Repositories;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id);
    Task<List<Order>> GetByCustomerIdAsync(Guid customerId, int page, int pageSize);
    Task<int> CountByCustomerIdAsync(Guid customerId);
    Task<List<Order>> GetAllAsync(int page, int pageSize);
    Task<int> CountAllAsync();
    void Add(Order order);
    Task SaveChangesAsync();
}
