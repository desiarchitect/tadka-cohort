using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;

namespace Tadka.Api.Tests.Architecture;

/// <summary>
/// Architecture tests that ENFORCE the boundary rules we otherwise check by eye (ADR-008/022/024).
/// These build the EF model + reflect over the assembly — no database, no Docker, instant. They turn
/// "we promise there are no cross-schema FKs / no payment code in the monolith" into a failing build
/// the moment someone breaks it (e.g. an EF navigation that implicitly creates a cross-schema FK).
/// </summary>
public class BoundaryTests
{
    private static TadkaDbContext BuildContext() =>
        // A provider is needed to build the model; no connection is opened (we only read metadata).
        new(new DbContextOptionsBuilder<TadkaDbContext>()
            .UseNpgsql("Host=localhost;Database=_model_only_;Username=x;Password=x")
            .Options);

    [Fact]
    public void No_foreign_key_crosses_a_schema_boundary()  // ADR-008
    {
        using var ctx = BuildContext();
        var model = ctx.Model;
        var defaultSchema = model.GetDefaultSchema();

        var offenders = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            var dependentSchema = entity.GetSchema() ?? defaultSchema;
            foreach (var fk in entity.GetForeignKeys())
            {
                var principalSchema = fk.PrincipalEntityType.GetSchema() ?? defaultSchema;
                if (dependentSchema is not null && principalSchema is not null && dependentSchema != principalSchema)
                    offenders.Add($"{entity.DisplayName()} [{dependentSchema}] → {fk.PrincipalEntityType.DisplayName()} [{principalSchema}]");
            }
        }

        Assert.True(offenders.Count == 0,
            "Cross-schema foreign key(s) found — ADR-008 forbids these (cross-domain refs are by ID only):\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void Monolith_does_not_contain_Payment_internals()  // ADR-022/024 — extracted on Day 8
    {
        var assembly = typeof(TadkaDbContext).Assembly;
        var typeNames = assembly.GetTypes().Select(t => t.Name).ToHashSet();

        // The Payment gateway, service, and DbContext moved to Tadka.Payment.Api — they must not be here.
        Assert.DoesNotContain("PaymentDbContext", typeNames);
        Assert.DoesNotContain("FakePaymentGateway", typeNames);
        Assert.DoesNotContain("IPaymentGateway", typeNames);
        // The payment domain entity is owned by the Payment service now.
        Assert.Null(assembly.GetType("Tadka.Api.Domain.Payments.Payment"));

        // What the monolith MAY keep is the client-side seam only (talks over the contract).
        Assert.Contains("IPaymentClient", typeNames);
    }

    [Fact]
    public void Ordering_domain_does_not_reference_Payment_types()  // ADR-022/024
    {
        var assembly = typeof(TadkaDbContext).Assembly;
        var orderingTypes = assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Tadka.Api.Domain.Orders"))
            .ToList();

        var leaks = new List<string>();
        foreach (var type in orderingTypes)
        {
            // Any member that exposes a payment-module type would couple Ordering to Payment.
            var referenced = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(MemberTypeName)
                .Where(n => n is not null && n!.Contains("Modules.Payments"));
            foreach (var r in referenced)
                leaks.Add($"{type.FullName} → {r}");
        }

        Assert.True(leaks.Count == 0,
            "Ordering domain references a Payment type — it must talk only via shared events (ADR-022/024):\n  " +
            string.Join("\n  ", leaks));
    }

    private static string? MemberTypeName(MemberInfo m) => m switch
    {
        PropertyInfo p => p.PropertyType.FullName,
        FieldInfo f => f.FieldType.FullName,
        MethodInfo me => me.ReturnType.FullName,
        _ => null
    };
}
