using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Ledgerline.Domain;

/// <summary>
/// An ISO 4217 currency and the number of minor units it uses (EUR: 2, JPY: 0, KWD: 3).
/// Instances are flyweights: there is exactly one <see cref="Currency"/> per code.
/// </summary>
public sealed class Currency : IEquatable<Currency>
{
    private static readonly FrozenDictionary<string, Currency> Supported = new Currency[]
    {
        new("EUR", 2), new("USD", 2), new("GBP", 2), new("CHF", 2), new("SEK", 2), new("NOK", 2),
        new("DKK", 2), new("PLN", 2), new("CZK", 2), new("HUF", 2), new("RON", 2), new("CAD", 2),
        new("AUD", 2), new("NZD", 2), new("BRL", 2), new("MXN", 2), new("ZAR", 2), new("TRY", 2),
        new("INR", 2), new("JPY", 0), new("KRW", 0), new("ISK", 0), new("CLP", 0), new("KWD", 3),
        new("BHD", 3), new("JOD", 3), new("TND", 3),
    }.ToFrozenDictionary(c => c.Code, StringComparer.Ordinal);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>The three-letter upper-case ISO 4217 code.</summary>
    public string Code { get; }

    /// <summary>Number of digits after the decimal separator (the ISO 4217 "exponent").</summary>
    public int MinorUnits { get; }

    public static Currency EUR => Supported["EUR"];

    public static Currency USD => Supported["USD"];

    public static Currency GBP => Supported["GBP"];

    public static Currency JPY => Supported["JPY"];

    public static Currency KWD => Supported["KWD"];

    public static IReadOnlyCollection<Currency> All => Supported.Values;

    /// <summary>Returns the currency for an upper-case ISO 4217 code, or throws a validation error.</summary>
    public static Currency Of(string? code) =>
        TryParse(code, out var currency)
            ? currency
            : throw new LedgerValidationException("currency", $"'{code}' is not a supported ISO 4217 currency code.");

    public static bool TryParse([NotNullWhen(true)] string? code, [NotNullWhen(true)] out Currency? currency)
    {
        currency = null;
        return code is { Length: 3 } && Supported.TryGetValue(code, out currency);
    }

    public bool Equals(Currency? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    public override string ToString() => Code;

    public static bool operator ==(Currency? left, Currency? right) => Equals(left, right);

    public static bool operator !=(Currency? left, Currency? right) => !Equals(left, right);
}
