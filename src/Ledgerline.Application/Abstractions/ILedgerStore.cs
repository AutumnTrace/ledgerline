using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;

namespace Ledgerline.Application.Abstractions;

/// <summary>
/// The write-side unit of work. One instance per command attempt; <see cref="SaveChangesAsync"/>
/// commits the entry, the balances it moved and its outbox messages in a single transaction.
/// </summary>
public interface ILedgerStore
{
    Task<JournalEntry?> FindEntryAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<bool> IsReversedAsync(Guid entryId, CancellationToken cancellationToken);

    /// <summary>Loads an account by its natural key, opening it (unsaved) on first use.</summary>
    Task<Account> GetOrOpenAccountAsync(AccountKey key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken);

    Task<Withdrawal?> FindWithdrawalAsync(string withdrawalId, CancellationToken cancellationToken);

    void Add(JournalEntry entry);

    void Add(Withdrawal withdrawal);

    /// <summary>
    /// Commits the unit of work. Throws <see cref="TransientWriteConflictException"/> when a concurrent
    /// transaction won the race (stale version, duplicate key); the dispatcher then retries the command.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
