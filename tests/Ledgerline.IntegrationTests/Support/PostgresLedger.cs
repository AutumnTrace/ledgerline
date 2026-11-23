using Ledgerline.Application;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Dispatching;
using Ledgerline.Infrastructure;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledgerline.IntegrationTests.Support;

/// <summary>The application and infrastructure layers on a real PostgreSQL database, migrated from scratch.</summary>
internal sealed class PostgresLedger : IAsyncDisposable
{
    private PostgresLedger(ServiceProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public ServiceProvider Provider { get; }

    public string ConnectionString { get; }

    public static async Task<PostgresLedger> CreateAsync(ContainersFixture containers, IDictionary<string, string?>? overrides = null)
    {
        var connectionString = await containers.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = connectionString,
            ["Database:Provider"] = "Postgres",
            ["Ledger:Concurrency:MaxRetries"] = "10",
            ["Ledger:Concurrency:BaseDelay"] = "00:00:00.005",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddMetrics()
            .AddLedgerApplication()
            .AddLedgerInfrastructure(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await provider.InitializeLedgerDatabaseAsync(TestContext.Current.CancellationToken);
        return new PostgresLedger(provider, connectionString);
    }

    public Task<PostingResult> SendAsync<TCommand>(TCommand command)
        where TCommand : class, ICommand =>
        Provider.GetRequiredService<CommandDispatcher>().SendAsync(command, TestContext.Current.CancellationToken);

    public async Task<T> QueryAsync<T>(Func<LedgerDbContext, Task<T>> query)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<LedgerDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();

        // Each test class has its own database, so its pooled connections are useless afterwards.
        await using var connection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }
}
