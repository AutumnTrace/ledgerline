using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;
using Ledgerline.Infrastructure.Outbox;
using Ledgerline.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ledgerline.Infrastructure.Persistence;

/// <summary>
/// The write model, plus the outbox, inbox and projection tables that must share its transactions.
/// Queries go through Dapper on the read side and never load these entities.
/// </summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    public DbSet<Posting> Postings => Set<Posting>();

    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<RoundSummary> RoundSummaries => Set<RoundSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Currency>().HaveConversion<CurrencyConverter>().HaveMaxLength(3);

        // Enums are stored by name: readable in SQL, and reordering an enum can never corrupt history.
        configurationBuilder.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EntryType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<WithdrawalStatus>().HaveConversion<string>().HaveMaxLength(16);
    }

    private sealed class CurrencyConverter() : ValueConverter<Currency, string>(c => c.Code, code => Currency.Of(code));
}
