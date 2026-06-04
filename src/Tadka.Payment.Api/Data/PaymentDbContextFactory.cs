using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tadka.Payment.Api.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the context WITHOUT running the app
/// (no startup migration, no live DB needed). Only used by the EF tools — never at runtime.
/// </summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Port=5434;Database=tadka_payment;Username=tadka;Password=tadka_local")
            .Options;
        return new PaymentDbContext(options);
    }
}
