using Ledgerline.Application;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Dispatching;
using Ledgerline.Infrastructure;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Tests.Support;

/// <summary>The application and infrastructure layers wired exactly as the API wires them, on a temp SQLite file.</summary>
internal sealed class LedgerServices : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database;

    private LedgerServices(ServiceProvider provider, SqliteTestDatabase database)
    {
        Provider = provider;
        _database = database;
    }

    public ServiceProvider Provider { get; }

    public CommandDispatcher Dispatcher => Provider.GetRequiredService<CommandDispatcher>();

    public static async Task<LedgerServices> CreateAsync(IDictionary<string, string?>? overrides = null)
    {
        var database = new SqliteTestDatabase();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = database.ConnectionString,
            ["Database:Provider"] = "Sqlite",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddMetrics()
            .AddLedgerApplication()
            .AddLedgerInfrastructure(configuration);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.InitializeLedgerDatabaseAsync(TestContext.Current.CancellationToken);
        return new LedgerServices(provider, database);
    }

    public Task<PostingResult> SendAsync<TCommand>(TCommand command)
        where TCommand : class, ICommand =>
        Dispatcher.SendAsync(command, TestContext.Current.CancellationToken);

    /// <summary>Runs a query against a fresh DbContext, outside any command's unit of work.</summary>
    public async Task<T> QueryAsync<T>(Func<LedgerDbContext, Task<T>> query)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<LedgerDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        _database.Dispose();
    }
}
