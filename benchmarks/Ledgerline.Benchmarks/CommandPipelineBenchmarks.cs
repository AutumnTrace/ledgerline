using BenchmarkDotNet.Attributes;
using Ledgerline.Application;
using Ledgerline.Application.Commands;
using Ledgerline.Application.Dispatching;
using Ledgerline.Domain;
using Ledgerline.Infrastructure;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Benchmarks;

/// <summary>
/// One command end to end through the dispatcher on a SQLite file in WAL mode: idempotency lookup,
/// account loads, posting, outbox row and commit. Single-threaded; the disk flush on commit is included.
/// </summary>
[MemoryDiagnoser]
public class CommandPipelineBenchmarks
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ledgerline-bench-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;
    private CommandDispatcher _dispatcher = null!;
    private long _sequence;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = $"Data Source={_path}",
            ["Database:Provider"] = "Sqlite",
        }).Build();

        _services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddMetrics()
            .AddLedgerApplication()
            .AddLedgerInfrastructure(configuration)
            .BuildServiceProvider();
        await _services.InitializeLedgerDatabaseAsync();

        _dispatcher = _services.GetRequiredService<CommandDispatcher>();
        await _dispatcher.SendAsync(new SettleDeposit("bench-deposit", "northwind-pay", "p-bench", new Money(1_000_000_000_000, Currency.EUR)), CancellationToken.None);
        await _dispatcher.SendAsync(new PlaceBet("bench-replay", "p-bench", new Money(1, Currency.EUR), "r-bench"), CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    /// <summary>A new bet: every step of the write path, ending in a committed transaction.</summary>
    [Benchmark(Baseline = true)]
    public Task<PostingResult> PlaceBet() =>
        _dispatcher.SendAsync(new PlaceBet($"bench-{Interlocked.Increment(ref _sequence)}", "p-bench", new Money(1, Currency.EUR), "r-bench"), CancellationToken.None);

    /// <summary>A retried bet: found by its idempotency key and answered from the stored entry, nothing written.</summary>
    [Benchmark]
    public Task<PostingResult> ReplayBet() =>
        _dispatcher.SendAsync(new PlaceBet("bench-replay", "p-bench", new Money(1, Currency.EUR), "r-bench"), CancellationToken.None);
}
