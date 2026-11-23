using Ledgerline.Application.Abstractions;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;

namespace Ledgerline.Tests.Support;

/// <summary>
/// A dictionary-backed <see cref="ILedgerStore"/> for property tests: thousands of command sequences
/// per second, no database. It enforces the same unique keys the database does.
/// </summary>
internal sealed class InMemoryLedgerStore(TimeProvider clock) : ILedgerStore
{
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Withdrawal> _withdrawals = new(StringComparer.Ordinal);
    private readonly List<object> _pending = [];

    public IReadOnlyCollection<Account> Accounts => _accounts.Values;

    public IReadOnlyCollection<JournalEntry> Entries => _entries.Values;

    public IReadOnlyCollection<Withdrawal> Withdrawals => _withdrawals.Values;

    public Task<JournalEntry?> FindEntryAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetValueOrDefault(idempotencyKey));

    public Task<bool> IsReversedAsync(Guid entryId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.Values.Any(e => e.ReversesEntryId == entryId));

    public Task<Account> GetOrOpenAccountAsync(AccountKey key, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(key.Code, out var account))
        {
            account = Account.Open(key, clock.GetUtcNow());
            _accounts.Add(key.Code, account);
        }

        return Task.FromResult(account);
    }

    public Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.ToHashSet();
        IReadOnlyDictionary<Guid, Account> found = _accounts.Values.Where(a => ids.Contains(a.Id)).ToDictionary(a => a.Id);
        return Task.FromResult(found);
    }

    public Task<Withdrawal?> FindWithdrawalAsync(string withdrawalId, CancellationToken cancellationToken) =>
        Task.FromResult(_withdrawals.GetValueOrDefault(withdrawalId));

    public void Add(JournalEntry entry) => _pending.Add(entry);

    public void Add(Withdrawal withdrawal) => _pending.Add(withdrawal);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var item in _pending)
        {
            switch (item)
            {
                case JournalEntry entry:
                    _entries.Add(entry.IdempotencyKey, entry);
                    break;
                case Withdrawal withdrawal:
                    _withdrawals.Add(withdrawal.WithdrawalId, withdrawal);
                    break;
            }
        }

        _pending.Clear();
        return Task.CompletedTask;
    }
}
