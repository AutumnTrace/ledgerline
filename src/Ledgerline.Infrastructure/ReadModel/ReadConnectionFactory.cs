using System.Data.Common;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledgerline.Infrastructure.ReadModel;

/// <summary>
/// Opens plain ADO.NET connections for the read side. On PostgreSQL it can point at a read replica
/// (<c>ConnectionStrings:LedgerRead</c>); otherwise it shares the primary.
/// </summary>
public sealed class ReadConnectionFactory : IAsyncDisposable
{
    private readonly DatabaseProvider _provider;
    private readonly string _connectionString;
    private readonly Lazy<NpgsqlDataSource> _postgres;

    public ReadConnectionFactory(IOptions<DatabaseOptions> options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        _provider = options.Value.Provider;
        _connectionString = configuration.GetConnectionString("LedgerRead") is { Length: > 0 } replica
            ? replica
            : options.Value.ConnectionString;
        _postgres = new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(_connectionString));
        DapperSetup.EnsureConfigured();
    }

    public DatabaseProvider Provider => _provider;

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = _provider == DatabaseProvider.Postgres
            ? _postgres.Value.CreateConnection()
            : new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres.IsValueCreated)
        {
            await _postgres.Value.DisposeAsync();
        }
    }
}
