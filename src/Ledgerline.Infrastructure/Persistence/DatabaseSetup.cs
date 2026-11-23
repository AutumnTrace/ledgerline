using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ledgerline.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>Zero-setup local mode: a single file next to the app.</summary>
    Sqlite,

    /// <summary>Production target, with migrations, covering and partial indexes.</summary>
    Postgres,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Bound from <c>ConnectionStrings:Ledger</c>.</summary>
    [Required]
    public string ConnectionString { get; set; } = "Data Source=ledgerline.db";

    /// <summary>Apply migrations (PostgreSQL) or create the schema (SQLite) when the app starts.</summary>
    public bool InitializeOnStartup { get; set; } = true;
}

public static class DatabaseSetup
{
    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString)
    {
        _ = provider switch
        {
            DatabaseProvider.Postgres => builder.UseNpgsql(connectionString),
            DatabaseProvider.Sqlite => builder.UseSqlite(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

        return builder.UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// PostgreSQL gets versioned migrations; SQLite (local runs and fast tests) gets the schema created
    /// straight from the model and switched to WAL so readers never block the writer.
    /// </summary>
    public static async Task InitializeLedgerDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.InitializeOnStartup)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        if (options.Provider == DatabaseProvider.Postgres)
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }
}

/// <summary>Used by <c>dotnet ef migrations</c>. Migrations target PostgreSQL; no connection is opened.</summary>
internal sealed class DesignTimeLedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        DatabaseSetup.Configure(builder, DatabaseProvider.Postgres, "Host=localhost;Database=ledgerline;Username=ledgerline;Password=ledgerline");
        return new LedgerDbContext(builder.Options);
    }
}
