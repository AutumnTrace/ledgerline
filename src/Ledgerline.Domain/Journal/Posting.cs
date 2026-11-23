using Ledgerline.Domain.Accounts;

namespace Ledgerline.Domain.Journal;

/// <summary>
/// One leg of a journal entry: a signed amount moved into (+) or out of (-) one account.
/// Postings are immutable once written; corrections are new, reversing entries.
/// </summary>
public sealed class Posting
{
    private Posting()
    {
    }

    internal Posting(JournalEntry entry, Account account, Money amount, long? balanceAfterMinor)
    {
        EntryId = entry.Id;
        AccountId = account.Id;
        Currency = amount.Currency;
        AmountMinor = amount.Minor;
        BalanceAfterMinor = balanceAfterMinor;
        EntryType = entry.Type;
        PostedAt = entry.PostedAt;
    }

    /// <summary>Database-generated, monotonically increasing. Doubles as the keyset cursor for statements.</summary>
    public long Id { get; private set; }

    public Guid EntryId { get; private set; }

    public Guid AccountId { get; private set; }

    public Currency Currency { get; private set; } = null!;

    public long AmountMinor { get; private set; }

    /// <summary>The account's balance right after this posting (player wallets only).</summary>
    public long? BalanceAfterMinor { get; private set; }

    /// <summary>Copied from the entry so an account statement is a single index-only scan.</summary>
    public EntryType EntryType { get; private set; }

    /// <summary>Copied from the entry for the same reason as <see cref="EntryType"/>.</summary>
    public DateTimeOffset PostedAt { get; private set; }

    public Money Amount => new(AmountMinor, Currency);

    public Money? BalanceAfter => BalanceAfterMinor is { } balance ? new Money(balance, Currency) : null;
}
