namespace Tadka.Api.Infrastructure.Pii;

/// <summary>
/// Masks PII before it reaches logs/traces (ADR-032) — logs are a top real-world PII leak vector.
/// <c>+919876500001 → +91••••••01</c>, <c>priya@tadka.test → p***@tadka.test</c>. In production this is a
/// Serilog destructuring policy / log enricher; here it's an explicit helper so the masking is visible.
/// </summary>
public static class PiiMasker
{
    public static string Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "(none)";
        return phone.Length <= 5 ? "••" : phone[..3] + new string('•', phone.Length - 5) + phone[^2..];
    }

    public static string Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return "•••";
        var parts = email.Split('@');
        var user = parts[0];
        return (user.Length <= 1 ? user : user[..1] + "***") + "@" + parts[1];
    }

    public static string Address(string? _) => "•••(masked address)";
}
