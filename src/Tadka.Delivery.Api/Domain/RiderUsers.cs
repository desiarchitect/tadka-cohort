namespace Tadka.Delivery.Api.Domain;

/// <summary>
/// The login ids (JWT <c>sub</c>) of the three seeded riders. The same values are seeded as
/// <c>DeliveryAgent</c> users by the monolith's <c>AuthSeeder</c>. They are duplicated by hand across the
/// service boundary, the same convention as the Kafka message records (no shared project, ADR-033).
/// </summary>
public static class RiderUsers
{
    public static readonly Guid Suresh  = new("f1000000-0000-4000-8000-000000000001");
    public static readonly Guid Lakshmi = new("f1000000-0000-4000-8000-000000000002");
    public static readonly Guid Imran   = new("f1000000-0000-4000-8000-000000000003");
}
