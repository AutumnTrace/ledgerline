using Ledgerline.Infrastructure.Outbox;
using Ledgerline.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledgerline.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();
        builder.Property(m => m.Topic).HasMaxLength(249);
        builder.Property(m => m.MessageKey).HasMaxLength(64);
        builder.Property(m => m.MessageType).HasMaxLength(100);
        builder.Property(m => m.TraceParent).HasMaxLength(64);
        builder.Property(m => m.LastError).HasMaxLength(2000);

        builder.HasIndex(m => m.MessageId).IsUnique();

        // The dispatcher's only query: "next N unpublished, in order". The partial index holds just the
        // backlog, so it stays tiny however many millions of published rows accumulate.
        builder.HasIndex(m => m.Id)
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_at IS NULL");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");
        builder.HasKey(m => new { m.Consumer, m.MessageId });
        builder.Property(m => m.Consumer).HasMaxLength(100);
    }
}

internal sealed class RoundSummaryConfiguration : IEntityTypeConfiguration<RoundSummary>
{
    public void Configure(EntityTypeBuilder<RoundSummary> builder)
    {
        builder.ToTable("round_summaries");
        builder.HasKey(r => r.RoundId);
        builder.Property(r => r.RoundId).HasMaxLength(64);
        builder.Property(r => r.PlayerId).HasMaxLength(64);
        builder.Property(r => r.Currency).HasMaxLength(3);
    }
}
