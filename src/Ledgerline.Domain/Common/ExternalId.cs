using System.Text.RegularExpressions;

namespace Ledgerline.Domain;

/// <summary>
/// Validation for identifiers that come from other systems (player ids, transaction ids, round ids,
/// provider codes). They end up inside account codes and idempotency keys, so the alphabet is strict.
/// </summary>
public static partial class ExternalId
{
    public const string Pattern = "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";
    public const string ProviderPattern = "^[a-z0-9][a-z0-9-]{0,31}$";

    public static string Require(string? value, string field) =>
        value is not null && IdRegex().IsMatch(value)
            ? value
            : throw new LedgerValidationException(field, $"{field} must be 1-64 characters: letters, digits, '.', '_' or '-'.");

    public static string RequireProvider(string? value, string field = "provider") =>
        value is not null && ProviderRegex().IsMatch(value)
            ? value
            : throw new LedgerValidationException(field, $"{field} must be 1-32 characters: lower-case letters, digits or '-'.");

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(ProviderPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProviderRegex();
}
