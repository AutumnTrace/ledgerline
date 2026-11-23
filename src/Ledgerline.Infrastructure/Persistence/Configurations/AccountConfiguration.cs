using Ledgerline.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledgerline.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        // Defence in depth: the domain already refuses overdrafts, the database refuses them too.
        builder.ToTable("accounts", t => t.HasCheckConstraint("ck_accounts_wallet_not_negative", "balance_minor IS NULL OR balance_minor >= 0"));

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Code).HasMaxLength(160);
        builder.Property(a => a.OwnerId).HasMaxLength(64);
        builder.Property(a => a.Version).IsConcurrencyToken();

        // Natural-key lookup on every command; also what makes "open on first use" race-safe.
        builder.HasIndex(a => a.Code).IsUnique();

        // "All wallets of player X" on the read side. Operator accounts have no owner and stay out of it.
        builder.HasIndex(a => new { a.OwnerId, a.Type }).HasFilter("owner_id IS NOT NULL");

        builder.Ignore(a => a.Balance);
        builder.Ignore(a => a.TracksBalance);
    }
}
