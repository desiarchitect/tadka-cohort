using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tadka.Api.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the context WITHOUT running the app. Without it
/// the EF tools boot the whole host, and this app's startup runs <c>Database.Migrate()</c> against whatever is
/// listening on the configured port (a real, possibly running, database). Only used by the EF tools, never at
/// runtime. The connection string is never opened while adding a migration.
/// </summary>
public sealed class TadkaDbContextFactory : IDesignTimeDbContextFactory<TadkaDbContext>
{
    public TadkaDbContext CreateDbContext(string[] args)
    {
        // Field encryption is a value converter on Users.Phone (UserConfiguration reads FieldCipher.Enabled while
        // the model is built). The column type does not depend on the flag, but configure it like Program.cs does
        // so the design-time model is the same model the app runs.
        Infrastructure.Security.FieldCipher.Configure(true, "0EIJyWPct1+0ncRmpqJXxQ8AKEviFdz8+rw8PGqxKk0=");

        var options = new DbContextOptionsBuilder<TadkaDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local")
            .Options;
        return new TadkaDbContext(options);
    }
}
