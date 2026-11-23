namespace Ledgerline.Domain.Withdrawals;

public enum WithdrawalStatus
{
    /// <summary>Money is held in PSP suspense, waiting for the provider.</summary>
    Reserved,

    Completed,

    Reversed,
}

/// <summary>
/// The lifecycle of a payout: <c>Reserved → Completed</c> or <c>Reserved → Reversed</c>.
/// Each transition is backed by a journal entry; this aggregate only tracks which one is allowed next.
/// </summary>
public sealed class Withdrawal
{
    private Withdrawal()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The caller's id for the payout, unique across the ledger.</summary>
    public string WithdrawalId { get; private set; } = null!;

    public string PlayerId { get; private set; } = null!;

    public string Provider { get; private set; } = null!;

    public Currency Currency { get; private set; } = null!;

    public long AmountMinor { get; private set; }

    public WithdrawalStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid ReserveEntryId { get; private set; }

    public Guid? ResolutionEntryId { get; private set; }

    public string? ReversalReason { get; private set; }

    /// <summary>Optimistic concurrency token: a racing complete and reverse cannot both win.</summary>
    public long Version { get; private set; }

    public Money Amount => new(AmountMinor, Currency);

    public static Withdrawal Reserve(string withdrawalId, string playerId, string provider, Money amount, Guid reserveEntryId, DateTimeOffset now)
    {
        if (!amount.IsPositive)
        {
            throw new LedgerValidationException("amountMinor", "A withdrawal must be a positive amount.");
        }

        return new Withdrawal
        {
            Id = Guid.CreateVersion7(now),
            WithdrawalId = ExternalId.Require(withdrawalId, "withdrawalId"),
            PlayerId = ExternalId.Require(playerId, "playerId"),
            Provider = ExternalId.RequireProvider(provider),
            Currency = amount.Currency,
            AmountMinor = amount.Minor,
            Status = WithdrawalStatus.Reserved,
            RequestedAt = now,
            ReserveEntryId = reserveEntryId,
        };
    }

    public void EnsurePending()
    {
        if (Status != WithdrawalStatus.Reserved)
        {
            throw new InvalidLedgerStateException($"Withdrawal {WithdrawalId} is already {Status}.");
        }
    }

    public void Complete(Guid entryId, DateTimeOffset now)
    {
        EnsurePending();
        Status = WithdrawalStatus.Completed;
        Resolve(entryId, now);
    }

    public void Reverse(Guid entryId, string reason, DateTimeOffset now)
    {
        EnsurePending();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200)
        {
            throw new LedgerValidationException("reason", "A reversal needs a reason of at most 200 characters.");
        }

        Status = WithdrawalStatus.Reversed;
        ReversalReason = reason;
        Resolve(entryId, now);
    }

    private void Resolve(Guid entryId, DateTimeOffset now)
    {
        ResolutionEntryId = entryId;
        ResolvedAt = now;
        Version++;
    }
}
