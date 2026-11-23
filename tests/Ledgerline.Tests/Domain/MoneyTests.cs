using Ledgerline.Domain;

namespace Ledgerline.Tests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("10.50", "EUR", 1050)]
    [InlineData("0.01", "EUR", 1)]
    [InlineData("1000", "JPY", 1000)]
    [InlineData("1.250", "KWD", 1250)]
    [InlineData("-3.20", "USD", -320)]
    public void FromMajor_converts_exactly_into_minor_units(string major, string currency, long expectedMinor)
    {
        var money = Money.FromMajor(decimal.Parse(major, System.Globalization.CultureInfo.InvariantCulture), Currency.Of(currency));

        Assert.Equal(expectedMinor, money.Minor);
    }

    [Theory]
    [InlineData("10.501", "EUR")]
    [InlineData("1.5", "JPY")]
    [InlineData("0.0001", "KWD")]
    public void FromMajor_rejects_more_precision_than_the_currency_has(string major, string currency)
    {
        var ex = Assert.Throws<LedgerValidationException>(
            () => Money.FromMajor(decimal.Parse(major, System.Globalization.CultureInfo.InvariantCulture), Currency.Of(currency)));

        Assert.Equal("amount", ex.Field);
    }

    [Fact]
    public void Formatting_uses_the_currency_exponent()
    {
        Assert.Equal("10.50 EUR", new Money(1050, Currency.EUR).ToString());
        Assert.Equal("1000 JPY", new Money(1000, Currency.JPY).ToString());
        Assert.Equal("1.250 KWD", new Money(1250, Currency.KWD).ToString());
        Assert.Equal("-0.05 EUR", new Money(-5, Currency.EUR).ToString());
    }

    [Fact]
    public void Arithmetic_never_mixes_currencies()
    {
        var euros = new Money(100, Currency.EUR);
        var dollars = new Money(100, Currency.USD);

        Assert.Throws<CurrencyMismatchException>(() => euros + dollars);
        Assert.Throws<CurrencyMismatchException>(() => euros - dollars);
        Assert.Throws<CurrencyMismatchException>(() => euros > dollars);
    }

    [Fact]
    public void Arithmetic_is_checked_for_overflow()
    {
        var max = new Money(long.MaxValue, Currency.EUR);

        Assert.Throws<OverflowException>(() => max + new Money(1, Currency.EUR));
        Assert.Throws<OverflowException>(() => new Money(long.MinValue, Currency.EUR).Negate());
    }

    [Fact]
    public void Equality_is_by_amount_and_currency()
    {
        Assert.Equal(new Money(1050, Currency.EUR), Money.FromMajor(10.50m, Currency.EUR));
        Assert.NotEqual(new Money(1050, Currency.EUR), new Money(1050, Currency.USD));
    }
}
