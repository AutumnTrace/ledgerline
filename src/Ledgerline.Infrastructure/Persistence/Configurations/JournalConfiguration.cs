using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledgerline.Infrastructure.Persistence.Configurations;

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(200);
        builder.Property(e => e.RequestHash).HasMaxLength(64).IsFixedLength();
        builder.Property(e => e.TransactionId).HasMaxLength(64);
        builder.Property(e => e.PlayerId).HasMaxLength(64);
        builder.Property(e => e.RoundId).HasMaxLength(64);

        // Idempotency is enforced here, not just checked in code: two racing requests with the same
        // key cannot both commit, and the loser's retry finds the winner's entry as a replay.
        builder.HasIndex(e => e.IdempotencyKey).IsUnique();

        // An entry can be reversed at most once, even under concurrent rollbacks.
        builder.HasIndex(e => e.ReversesEntryId).IsUnique().HasFilter("reverses_entry_id IS NOT NULL");
        builder.HasOne<JournalEntry>().WithMany().HasForeignKey(e => e.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Postings).WithOne().HasForeignKey(p => p.EntryId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(e => e.Postings).HasField("_postings");

        builder.Ignore(e => e.DomainEvents);
    }
}

internal sealed class PostingConfiguration : IEntityTypeConfiguration<Posting>
{
    public void Configure(EntityTypeBuilder<Posting> builder)
    {
        builder.ToTable("postings", t => t.HasCheckConstraint("ck_postings_amount_not_zero", "amount_minor <> 0"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Restrict);

        // Covering index for account statements: keyset pagination walks (account_id, id) backwards
        // and every column the statement returns is in the INCLUDE list, so PostgreSQL answers a page
        // with an index-only scan. It also serves the derived balance of operator accounts.
        builder.HasIndex(p => new { p.AccountId, p.Id })
            .HasDatabaseName("ix_postings_account_statement")
            .IncludeProperties(p => new { p.EntryId, p.EntryType, p.AmountMinor, p.BalanceAfterMinor, p.PostedAt });

        builder.Ignore(p => p.Amount);
        builder.Ignore(p => p.BalanceAfter);
    }
}
