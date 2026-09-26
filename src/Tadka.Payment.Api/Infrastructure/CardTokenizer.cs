using System.Security.Cryptography;
using System.Text;

namespace Tadka.Payment.Api.Infrastructure;

/// <summary>
/// Converts a raw card number into an opaque token the moment it arrives, before it is logged or
/// persisted anywhere (ADR-046/PCI scope). The token is deterministic per card (same card -> same
/// token, useful for saved-card / repeat-charge flows) but does not encode the PAN in any recoverable
/// way — it is a one-way HMAC-SHA-256 digest, not encryption; there is nothing to decrypt back to a PAN.
/// Only the token and the last 4 digits (already public on the card and receipts) are ever stored.
///
/// KEYED, not a bare hash (fixed after a live security review found the earlier unkeyed-SHA-256 version
/// reversible by brute force): a card number's real entropy is small — roughly 6 unknown digits once the
/// issuer's BIN (the first 6-8 digits, publicly known) and the last 4 (stored right next to the token)
/// are accounted for, and the Luhn check digit removes 90% of what's left. That is on the order of
/// 100,000 SHA-256 computations per candidate BIN — well under a second on a laptop against a leaked
/// table of tokens. Keying the hash with a secret only this service holds (never derivable from the
/// database, unlike the BIN and last 4) makes that brute force infeasible: without the key, an attacker
/// cannot even compute a candidate digest to compare against a stolen token.
/// </summary>
public static class CardTokenizer
{
    private static byte[]? _key;

    /// <summary>
    /// Must be called once at startup (mirrors <c>FieldCipher.Configure</c>) before any charge is
    /// tokenized. The key is a secret exactly like a signing key or an encryption key (ADR-045) — it must
    /// never live next to the data it protects, and in production comes from a KMS or secrets manager,
    /// never from a config file in source control. The dev-only default below is exactly that: a default,
    /// never a real secret, same spirit as <c>FieldCipher</c>'s own fallback.
    /// </summary>
    public static void Configure(string base64Key) => _key = Convert.FromBase64String(base64Key);

    public static (string Token, string Last4) Tokenize(string cardNumber)
    {
        if (_key is null)
            throw new InvalidOperationException("CardTokenizer.Configure was not called with a key.");

        var digitsOnly = new string(cardNumber.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length < 4)
            throw new ArgumentException("Card number must have at least 4 digits.", nameof(cardNumber));

        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(digitsOnly));
        var token = "TOK-" + Convert.ToHexString(hash)[..16];
        var last4 = digitsOnly[^4..];

        return (token, last4);
    }
}
