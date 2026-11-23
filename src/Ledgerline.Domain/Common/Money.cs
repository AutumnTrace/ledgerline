using System.Globalization;

namespace Ledgerline.Domain;

/// <summary>
/// An amount of money in integer minor units (cents, öre, fils) of one ISO 4217 currency.
/// Arithmetic is checked: an overflow throws instead of wrapping, and currencies never mix.
/// </summary>
public readonly record struct Money
{
    private static readonly decimal[] PowersOfTen = [1m, 10m, 100m, 1_000m, 10_000m];

    public Money(long minor, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Minor = minor;
        Currency = currency;
    }

    /// <summary>The amount in minor units, e.g. 1050 for EUR 10.50.</summary>
    public long Minor { get; }

    public Currency Currency { get; }

    public bool IsZero => Minor == 0;

    public bool IsPositive => Minor > 0;

    public bool IsNegative => Minor < 0;

    public static Money Zero(Currency currency) => new(0, currency);

    /// <summary>
    /// Converts a decimal major-unit amount (10.50) into minor units. Rejects amounts with more
    /// precision than the currency allows instead of silently rounding them.
    /// </summary>
    public static Money FromMajor(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var scaled = amount * PowersOfTen[currency.MinorUnits];
        if (scaled != decimal.Truncate(scaled))
        {
            throw new LedgerValidationException(
                "amount",
                $"{amount.ToString(CultureInfo.InvariantCulture)} has more decimal places than {currency.Code} allows ({currency.MinorUnits}).");
        }

        if (scaled is > long.MaxValue or < long.MinValue)
        {
            throw new LedgerValidationException("amount", "Amount is outside the supported range.");
        }

        return new Money((long)scaled, currency);
    }

    /// <summary>The amount in major units (EUR 10.50 becomes 10.50m). Exact: decimal has enough precision for any long.</summary>
    public decimal ToMajor() => Minor / PowersOfTen[Currency.MinorUnits];

    public Money Negate() => new(checked(-Minor), Currency);

    public Money Abs() => Minor < 0 ? Negate() : this;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Minor + right.Minor), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Minor - right.Minor), left.Currency);
    }

    public static Money operator -(Money value) => value.Negate();

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Minor > right.Minor;
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Minor < right.Minor;
    }

    public static bool operator >=(Money left, Money right) => !(left < right);

    public static bool operator <=(Money left, Money right) => !(left > right);

    /// <summary>Major-unit amount with exactly the currency's minor digits, e.g. "10.50" or "1000".</summary>
    public string FormatAmount() =>
        ToMajor().ToString("F" + Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public override string ToString() => $"{FormatAmount()} {Currency.Code}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }
}
