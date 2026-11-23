using Ledgerline.Domain;

namespace Ledgerline.Tests.Domain;

public sealed class CurrencyTests
{
    [Fact]
    public void Of_returns_the_same_instance_for_a_code()
    {
        Assert.Same(Currency.EUR, Currency.Of("EUR"));
        Assert.Equal(2, Currency.Of("EUR").MinorUnits);
        Assert.Equal(0, Currency.Of("JPY").MinorUnits);
        Assert.Equal(3, Currency.Of("BHD").MinorUnits);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("XXX")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_malformed_codes_are_rejected(string? code)
    {
        Assert.False(Currency.TryParse(code, out _));
        var ex = Assert.Throws<LedgerValidationException>(() => Currency.Of(code));
        Assert.Equal("currency", ex.Field);
    }
}
