using System.ComponentModel.DataAnnotations;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;

namespace Ledgerline.Api.Contracts;

internal static class Limits
{
    /// <summary>10 billion major units of a two-decimal currency: generous, and far from overflowing a long when summed.</summary>
    public const string MaxAmountMinor = "1000000000000";

    public static Money Money(long amountMinor, string? currency) => new(amountMinor, Currency.Of(currency));
}

/// <summary>A payment provider confirmed a deposit.</summary>
public sealed class DepositRequest
{
    /// <summary>The provider's transaction id. Idempotency key, together with the provider.</summary>
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? TransactionId { get; init; }

    /// <summary>Provider code, e.g. <c>northwind-pay</c>.</summary>
    [Required, RegularExpression(ExternalId.ProviderPattern)]
    public string? Provider { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    /// <summary>ISO 4217 code, upper case.</summary>
    [Required, StringLength(3, MinimumLength = 3)]
    public string? Currency { get; init; }

    /// <summary>Amount in minor units (cents): 2500 is EUR 25.00.</summary>
    [Range(typeof(long), "1", Limits.MaxAmountMinor)]
    public long AmountMinor { get; init; }
}

/// <summary>Seamless-wallet bet or win from a game server.</summary>
public sealed class GameTransactionRequest
{
    /// <summary>Unique per game transaction. Bets, wins and rollbacks share this id space.</summary>
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? TransactionId { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? RoundId { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string? Currency { get; init; }

    [Range(typeof(long), "1", Limits.MaxAmountMinor)]
    public long AmountMinor { get; init; }
}

public sealed class RollbackRequest
{
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? TransactionId { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    /// <summary>The transaction id of the bet to cancel.</summary>
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? OriginalTransactionId { get; init; }
}

public sealed class WithdrawalRequest
{
    /// <summary>The caller's payout id. All later calls for this payout use it.</summary>
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? WithdrawalId { get; init; }

    [Required, RegularExpression(ExternalId.ProviderPattern)]
    public string? Provider { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string? Currency { get; init; }

    [Range(typeof(long), "1", Limits.MaxAmountMinor)]
    public long AmountMinor { get; init; }
}

public sealed class ReverseWithdrawalRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string? Reason { get; init; }
}

public sealed class BonusRequest
{
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? TransactionId { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string? Currency { get; init; }

    [Range(typeof(long), "1", Limits.MaxAmountMinor)]
    public long AmountMinor { get; init; }
}

public sealed class TransferRequest
{
    [Required, RegularExpression(ExternalId.Pattern)]
    public string? TransactionId { get; init; }

    [Required, RegularExpression(ExternalId.Pattern)]
    public string? PlayerId { get; init; }

    [Required]
    public Wallet? From { get; init; }

    [Required]
    public Wallet? To { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string? Currency { get; init; }

    [Range(typeof(long), "1", Limits.MaxAmountMinor)]
    public long AmountMinor { get; init; }
}
