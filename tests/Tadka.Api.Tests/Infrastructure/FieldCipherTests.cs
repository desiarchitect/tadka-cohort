using Tadka.Api.Infrastructure.Security;

namespace Tadka.Api.Tests.Infrastructure;

/// <summary>
/// Pure unit tests for AES-GCM field-level encryption (ADR-045) — no database needed.
/// </summary>
public class FieldCipherTests
{
    private const string Key = "0EIJyWPct1+0ncRmpqJXxQ8AKEviFdz8+rw8PGqxKk0=";

    public FieldCipherTests() => FieldCipher.Configure(enabled: true, base64Key: Key);

    [Fact]
    public void Encrypt_then_decrypt_round_trips_to_the_original_value()
    {
        var plaintext = "+919876500001";

        var ciphertext = FieldCipher.Encrypt(plaintext);
        var decrypted = FieldCipher.Decrypt(ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var plaintext = "+919876500001";

        var ciphertext = FieldCipher.Encrypt(plaintext);

        Assert.DoesNotContain(plaintext, ciphertext);
    }

    [Fact]
    public void Encrypting_the_same_value_twice_produces_different_ciphertext()
    {
        var plaintext = "+919876500001";

        var first = FieldCipher.Encrypt(plaintext);
        var second = FieldCipher.Encrypt(plaintext);

        // A fresh random nonce per write means two encryptions of the same phone number are NOT
        // identical — rows can't be correlated by matching encrypted values.
        Assert.NotEqual(first, second);

        // But both still decrypt back to the same plaintext.
        Assert.Equal(plaintext, FieldCipher.Decrypt(first));
        Assert.Equal(plaintext, FieldCipher.Decrypt(second));
    }

    [Fact]
    public void Configure_with_enabled_false_reports_disabled()
    {
        FieldCipher.Configure(enabled: false, base64Key: Key);

        Assert.False(FieldCipher.Enabled);

        FieldCipher.Configure(enabled: true, base64Key: Key); // restore for any other test relying on it
    }
}
