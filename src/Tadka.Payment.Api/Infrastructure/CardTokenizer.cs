using System.Security.Cryptography;
using System.Text;

namespace Tadka.Payment.Api.Infrastructure;

/// <summary>
/// Converts a raw card number into an opaque token the moment it arrives, before it is logged or
/// persisted anywhere (ADR-046/PCI scope). The token is deterministic per card (same card -> same
/// token, useful for saved-card / repeat-charge flows) but does not encode the PAN in any recoverable
/// way — it is a one-way SHA-256 digest, not encryption; there is nothing to decrypt back to a PAN. Only
/// the token and the last 4 digits (already public on the card and receipts) are ever stored.
/// </summary>
public static class CardTokenizer
{
    public static (string Token, string Last4) Tokenize(string cardNumber)
    {
        var digitsOnly = new string(cardNumber.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length < 4)
            throw new ArgumentException("Card number must have at least 4 digits.", nameof(cardNumber));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(digitsOnly));
        var token = "TOK-" + Convert.ToHexString(hash)[..16];
        var last4 = digitsOnly[^4..];

        return (token, last4);
    }
}
