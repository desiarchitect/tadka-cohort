using System.Text;

namespace Tadka.Api.Contracts.Orders;

/// <summary>
/// Opaque keyset-pagination cursor: (CreatedAt, Id) — the same composite the ORDER BY and the
/// (customer_id, created_at DESC) index (ADR-014) use, so a cursor query is an index range scan,
/// not a Sort + Skip. Base64-encoded so the client treats it as opaque, not a value to construct.
/// </summary>
internal static class OrderCursor
{
    public static string Encode(DateTime createdAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt:O}|{id}"));

    public static (DateTime CreatedAt, Guid Id) Decode(string cursor)
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            return (DateTime.Parse(parts[0]).ToUniversalTime(), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor), ex);
        }
    }
}
