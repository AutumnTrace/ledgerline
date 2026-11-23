namespace Ledgerline.Domain.Accounts;

/// <summary>
/// A ledger account. Money only moves in or out through postings of a balanced
/// <see cref="Journal.JournalEntry"/>; there is deliberately no public way to set a balance.
/// </summary>
/// <remarks>
/// Player wallets keep <see cref="BalanceMinor"/> up to date and bump <see cref="Version"/> on every
/// posting, which is what optimistic concurrency checks. Operator accounts (house, PSP clearing...)
/// are written by almost every transaction, so they keep no stored balance and are never updated:
/// that keeps them from becoming a single hot row that serialises the whole ledger.
/// </remarks>
public sealed class Account
{
    private Account()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, human-readable natural key, e.g. <c>player:p-1001:cash:EUR</c>.</summary>
    public string Code { get; private set; } = null!;

    public AccountType Type { get; private set; }

    public string? OwnerId { get; private set; }

    public Currency Currency { get; private set; } = null!;

    /// <summary>Stored balance for player wallets; null for operator accounts, whose balance is the sum of their postings.</summary>
    public long? BalanceMinor { get; private set; }

    /// <summary>Incremented on every balance change. Used as the optimistic concurrency token.</summary>
    public long Version { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public bool TracksBalance => Type.IsPlayerWallet();

    /// <summary>The stored balance. Only meaningful for player wallets.</summary>
    public Money Balance => TracksBalance
        ? new Money(BalanceMinor ?? 0, Currency)
        : throw new InvalidOperationException($"{Code} does not store its balance; sum its postings instead.");

    public static Account Open(AccountKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        now = LedgerTime.Normalize(now);
        return new Account
        {
            Id = Guid.CreateVersion7(now),
            Code = key.Code,
            Type = key.Type,
            OwnerId = key.Owner,
            Currency = key.Currency,
            BalanceMinor = key.Type.IsPlayerWallet() ? 0 : null,
            OpenedAt = now,
        };
    }

    /// <summary>Throws if applying <paramref name="amount"/> would break an invariant. Does not change state.</summary>
    internal void EnsureCanApply(Money amount)
    {
        if (amount.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, amount.Currency);
        }

        if (TracksBalance && checked(BalanceMinor!.Value + amount.Minor) < 0)
        {
            throw new InsufficientFundsException(Code, Balance, amount.Negate());
        }
    }

    /// <summary>Applies a posting amount and returns the balance after it (null for operator accounts).</summary>
    internal long? Apply(Money amount)
    {
        EnsureCanApply(amount);
        if (!TracksBalance)
        {
            return null;
        }

        BalanceMinor = checked(BalanceMinor!.Value + amount.Minor);
        Version++;
        return BalanceMinor;
    }
}
