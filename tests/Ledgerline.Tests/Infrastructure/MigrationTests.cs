using Ledgerline.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ledgerline.Tests.Infrastructure;

public sealed class MigrationTests
{
    [Fact]
    public void The_PostgreSQL_migrations_match_the_model()
    {
        // Fails when an entity configuration changes without `dotnet ef migrations add`.
        // Compares the model to the migrations snapshot; no database connection is opened.
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        DatabaseSetup.Configure(builder, DatabaseProvider.Postgres, "Host=unused");
        using var db = new LedgerDbContext(builder.Options);

        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project src/Ledgerline.Infrastructure");
    }

    [Fact]
    public void The_covering_and_partial_indexes_are_in_the_model()
    {
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        DatabaseSetup.Configure(builder, DatabaseProvider.Postgres, "Host=unused");
        using var db = new LedgerDbContext(builder.Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var indexes = model.GetEntityTypes().SelectMany(e => e.GetIndexes()).ToDictionary(i => i.GetDatabaseName()!);

        Assert.Equal("status = 'Reserved'", indexes["ix_withdrawals_pending"].GetFilter());
        Assert.Equal("reverses_entry_id IS NOT NULL", indexes["ix_journal_entries_reverses_entry_id"].GetFilter());
        Assert.True(indexes["ix_journal_entries_idempotency_key"].IsUnique);
        Assert.Equal(
            ["EntryId", "EntryType", "AmountMinor", "BalanceAfterMinor", "PostedAt"],
            (IEnumerable<string>?)indexes["ix_postings_account_statement"].FindAnnotation("Npgsql:IndexInclude")?.Value);
    }
}
