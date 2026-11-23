using Ledgerline.Domain.Accounts;

namespace Ledgerline.Domain.Journal;

/// <summary>What an entry is and why it exists, separate from the money it moves.</summary>
/// <param name="IdempotencyKey">Unique per business operation, e.g. <c>wallet:tx-123</c>. A replay finds the original entry by it.</param>
/// <param name="RequestHash">Fingerprint of the request that created the entry, to reject key reuse with a different payload.</param>
public sealed record EntryHeader(EntryType Type, string IdempotencyKey, string RequestHash, string TransactionId, string PlayerId)
{
    public string? RoundId { get; init; }

    /// <summary>The entry this one cancels (bet rollbacks). Unique: an entry can be reversed at most once.</summary>
    public Guid? ReversesEntryId { get; init; }
}

/// <summary>One leg of an entry before it is posted.</summary>
public readonly record struct Leg(Account Account, Money Amount);

/// <summary>
/// A balanced, immutable set of postings. The aggregate guarantees the core ledger invariant:
/// for every currency in the entry, the postings sum to exactly zero.
/// </summary>
public sealed class JournalEntry : AggregateRoot
{
    private readonly List<Posting> _postings = [];

    private JournalEntry()
    {
    }

    public Guid Id { get; private set; }

    public EntryType Type { get; private set; }

    public string IdempotencyKey { get; private set; } = null!;

    public string RequestHash { get; private set; } = null!;

    public string TransactionId { get; private set; } = null!;

    public string PlayerId { get; private set; } = null!;

    public string? RoundId { get; private set; }

    public Guid? ReversesEntryId { get; private set; }

    public DateTimeOffset PostedAt { get; private set; }

    public IReadOnlyList<Posting> Postings => _postings;

    /// <summary>
    /// Validates the legs, applies them to their accounts and returns the new entry.
    /// Every check runs before any account changes, so a rejected entry leaves no trace.
    /// </summary>
    public static JournalEntry Post(EntryHeader header, IReadOnlyList<Leg> legs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(legs);
        Validate(legs);
        now = LedgerTime.Normalize(now);

        foreach (var leg in legs)
        {
            leg.Account.EnsureCanApply(leg.Amount);
        }

        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(now),
            Type = header.Type,
            IdempotencyKey = header.IdempotencyKey,
            RequestHash = header.RequestHash,
            TransactionId = ExternalId.Require(header.TransactionId, "transactionId"),
            PlayerId = ExternalId.Require(header.PlayerId, "playerId"),
            RoundId = header.RoundId is null ? null : ExternalId.Require(header.RoundId, "roundId"),
            ReversesEntryId = header.ReversesEntryId,
            PostedAt = now,
        };

        var posted = new List<PostedLeg>(legs.Count);
        foreach (var leg in legs)
        {
            var balanceAfter = leg.Account.Apply(leg.Amount);
            entry._postings.Add(new Posting(entry, leg.Account, leg.Amount, balanceAfter));
            posted.Add(new PostedLeg(leg.Account.Id, leg.Account.Code, leg.Account.Type, leg.Amount.Currency.Code, leg.Amount.Minor, balanceAfter));
        }

        entry.Raise(new JournalEntryPosted(
            Guid.CreateVersion7(now),
            now,
            entry.Id,
            entry.Type,
            entry.TransactionId,
            entry.PlayerId,
            entry.RoundId,
            entry.ReversesEntryId,
            posted));

        return entry;
    }

    private static void Validate(IReadOnlyList<Leg> legs)
    {
        if (legs.Count < 2)
        {
            throw new UnbalancedEntryException("A journal entry needs at least two postings.");
        }

        var accounts = new HashSet<Guid>();
        var sums = new Dictionary<Currency, long>();
        foreach (var (account, amount) in legs)
        {
            ArgumentNullException.ThrowIfNull(account);
            if (amount.IsZero)
            {
                throw new LedgerValidationException("amount", "Every posting must move a non-zero amount.");
            }

            if (amount.Currency != account.Currency)
            {
                throw new CurrencyMismatchException(account.Currency, amount.Currency);
            }

            if (!accounts.Add(account.Id))
            {
                throw new UnbalancedEntryException($"Account {account.Code} appears more than once in the entry.");
            }

            sums[amount.Currency] = checked(sums.GetValueOrDefault(amount.Currency) + amount.Minor);
        }

        foreach (var (currency, sum) in sums)
        {
            if (sum != 0)
            {
                throw new UnbalancedEntryException($"Postings in {currency.Code} sum to {sum}, not zero.");
            }
        }
    }
}
