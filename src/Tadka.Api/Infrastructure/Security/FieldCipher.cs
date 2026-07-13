using System.Security.Cryptography;

namespace Tadka.Api.Infrastructure.Security;

/// <summary>
/// AES-GCM field-level encryption for at-rest PII columns (ADR-045). Configured once at startup from
/// <c>Demo:EncryptPiiAtRest</c> / <c>Demo:EncryptionKey</c> and read by <see cref="Data.Configurations.UserConfiguration"/>
/// while building the EF model — a static configuration point because <c>IEntityTypeConfiguration&lt;T&gt;</c>
/// instances created by <c>ApplyConfigurationsFromAssembly</c> have no constructor injection. Each ciphertext
/// is <c>nonce (12 bytes) + tag (16 bytes) + ciphertext</c>, base64-encoded — a fresh random nonce per write,
/// so encrypting the same phone number twice produces different ciphertext (no pattern to correlate rows by).
/// </summary>
public static class FieldCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[]? _key;

    public static bool Enabled { get; private set; }

    public static void Configure(bool enabled, string? base64Key)
    {
        Enabled = enabled && !string.IsNullOrWhiteSpace(base64Key);
        _key = Enabled ? Convert.FromBase64String(base64Key!) : null;
    }

    public static string Encrypt(string plaintext)
    {
        if (_key is null) throw new InvalidOperationException("FieldCipher.Configure was not called with a key.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, result, NonceSize + TagSize, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string ciphertextBase64)
    {
        if (_key is null) throw new InvalidOperationException("FieldCipher.Configure was not called with a key.");

        var all = Convert.FromBase64String(ciphertextBase64);
        var nonce = all[..NonceSize];
        var tag = all[NonceSize..(NonceSize + TagSize)];
        var cipherBytes = all[(NonceSize + TagSize)..];
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
