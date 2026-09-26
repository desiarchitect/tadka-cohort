using Tadka.Payment.Api.Infrastructure;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Pure unit tests for card tokenization (ADR-046) — no database or gateway needed.
/// </summary>
public class CardTokenizerTests
{
    // A keyed hash needs a key configured before use (mirrors FieldCipherTests) — any fixed test key
    // works here, since these tests only assert properties of the tokenization, not this exact value.
    private const string Key = "owMbZYDfyQY0WCnoguPMpVe7Zb/voograkyID97ppuY=";

    public CardTokenizerTests() => CardTokenizer.Configure(Key);

    [Fact]
    public void Tokenizing_the_same_card_number_twice_produces_the_same_token()
    {
        var (token1, last4_1) = CardTokenizer.Tokenize("4111 1111 1111 1111");
        var (token2, last4_2) = CardTokenizer.Tokenize("4111 1111 1111 1111");

        Assert.Equal(token1, token2); // deterministic — a saved card produces a stable identifier
        Assert.Equal(last4_1, last4_2);
    }

    [Fact]
    public void Different_card_numbers_produce_different_tokens()
    {
        var (token1, _) = CardTokenizer.Tokenize("4111111111111111");
        var (token2, _) = CardTokenizer.Tokenize("5500000000000004");

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void The_token_never_contains_the_raw_card_number()
    {
        var cardNumber = "4111111111111111";

        var (token, _) = CardTokenizer.Tokenize(cardNumber);

        Assert.DoesNotContain(cardNumber, token);
        Assert.DoesNotContain("4111111111111111", token);
    }

    [Fact]
    public void Last4_is_exactly_the_last_four_digits()
    {
        var (_, last4) = CardTokenizer.Tokenize("4111 1111 1111 1111");

        Assert.Equal("1111", last4);
    }

    [Fact]
    public void Non_digit_formatting_characters_do_not_change_the_token()
    {
        var (spaced, _) = CardTokenizer.Tokenize("4111 1111 1111 1111");
        var (dashed, _) = CardTokenizer.Tokenize("4111-1111-1111-1111");
        var (plain, _) = CardTokenizer.Tokenize("4111111111111111");

        Assert.Equal(spaced, dashed);
        Assert.Equal(spaced, plain);
    }

    [Fact]
    public void Too_few_digits_throws()
    {
        Assert.Throws<ArgumentException>(() => CardTokenizer.Tokenize("12"));
    }
}
