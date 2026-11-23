using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledgerline.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the write-side unit of work. On save, the domain events raised by the
/// aggregates become outbox rows in the same transaction: there is no window where the entry is
/// committed but its event is lost, or the event is published for an entry that rolled back.
/// </summary>
internal sealed class EfLedgerStore(LedgerDbContext db, TimeProvider clock, IOptions<MessagingOptions> messaging) : ILedgerStore
{
    public Task<JournalEntry?> FindEntryAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.JournalEntries
            .Include(e => e.Postings)
            .SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<bool> IsReversedAsync(Guid entryId, CancellationToken cancellationToken) =>
        db.JournalEntries.AnyAsync(e => e.ReversesEntryId == entryId, cancellationToken);

    public async Task<Account> GetOrOpenAccountAsync(AccountKey key, CancellationToken cancellationToken)
    {
        var code = key.Code;
        var account = db.Accounts.Local.FirstOrDefault(a => a.Code == code)
            ?? await db.Accounts.SingleOrDefaultAsync(a => a.Code == code, cancellationToken);

        if (account is null)
        {
            // Two first-time writers can both get here; the unique index on code lets exactly one
            // insert win and the other is retried, by which time the account exists.
            account = Account.Open(key, clock.GetUtcNow());
            db.Accounts.Add(account);
        }

        return account;
    }

    public async Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        return accounts.ToDictionary(a => a.Id);
    }

    public Task<Withdrawal?> FindWithdrawalAsync(string withdrawalId, CancellationToken cancellationToken) =>
        db.Withdrawals.SingleOrDefaultAsync(w => w.WithdrawalId == withdrawalId, cancellationToken);

    public void Add(JournalEntry entry) => db.JournalEntries.Add(entry);

    public void Add(Withdrawal withdrawal) => db.Withdrawals.Add(withdrawal);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var aggregates = db.ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0).ToList();
        foreach (var posted in aggregates.SelectMany(a => a.DomainEvents).OfType<JournalEntryPosted>())
        {
            db.OutboxMessages.Add(OutboxMessage.From(posted, messaging.Value.Topic));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            aggregates.ForEach(a => a.ClearDomainEvents());
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new TransientWriteConflictException("A concurrent transaction changed the same wallet or withdrawal.", ex);
        }
        catch (DbUpdateException ex) when (DbErrors.IsWriteConflict(ex))
        {
            throw new TransientWriteConflictException("A concurrent transaction inserted the same key first.", ex);
        }
    }
}

internal static class DbErrors
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    /// <summary>Errors that mean "someone else won the race", as opposed to a bug or a broken constraint.</summary>
    public static bool IsWriteConflict(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException pg => pg.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected,
        SqliteException sqlite => sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked
            || (sqlite.SqliteErrorCode == SqliteConstraint
                && sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey),
        _ => false,
    };
}
