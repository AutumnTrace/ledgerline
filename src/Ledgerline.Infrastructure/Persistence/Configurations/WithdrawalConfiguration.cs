using Ledgerline.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledgerline.Infrastructure.Persistence.Configurations;

internal sealed class WithdrawalConfiguration : IEntityTypeConfiguration<Withdrawal>
{
    public void Configure(EntityTypeBuilder<Withdrawal> builder)
    {
        builder.ToTable("withdrawals");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.WithdrawalId).HasMaxLength(64);
        builder.Property(w => w.PlayerId).HasMaxLength(64);
        builder.Property(w => w.Provider).HasMaxLength(32);
        builder.Property(w => w.ReversalReason).HasMaxLength(200);
        builder.Property(w => w.Version).IsConcurrencyToken();

        builder.HasIndex(w => w.WithdrawalId).IsUnique();

        // Partial index: only holds that are still open. Resolved withdrawals pile up forever, but this
        // index stays as small as the operations backlog, which is what the "pending payouts" view scans.
        builder.HasIndex(w => w.RequestedAt)
            .HasDatabaseName("ix_withdrawals_pending")
            .HasFilter("status = 'Reserved'");

        builder.Ignore(w => w.Amount);
    }
}
