using Ledgerline.Application;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Domain.Journal;
using Ledgerline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Tests.Application;

/// <summary>
/// Many writers on one wallet at once. On SQLite the database serialises writers, so these tests
/// exercise the optimistic-concurrency path (stale version, retry). The integration suite repeats
/// them on PostgreSQL, where row locks and READ COMMITTED make the races real.
/// </summary>
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private LedgerServices _ledger = null!;

    public async ValueTask InitializeAsync() =>
        _ledger = await LedgerServices.CreateAsync(new Dictionary<string, string?>
        {
            ["Ledger:Concurrency:MaxRetries"] = "15",
            ["Ledger:Concurrency:BaseDelay"] = "00:00:00.002",
        });

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task Parallel_bets_never_overdraw_a_wallet_and_every_cent_is_accounted_for()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));

        // 40 bets of 5.00 against 100.00: at most 20 can ever succeed.
        var outcomes = await InParallel(40, i => Outcome(() => _ledger.SendAsync(new PlaceBet($"bet-{i}", "p-1", Eur(500), "round-1"))));

        var posted = outcomes.OfType<PostingResult>().ToList();
        var insufficient = outcomes.OfType<InsufficientFundsException>().Count();
        var conflicts = outcomes.OfType<WriteConflictException>().Count();
        Assert.Equal(40, posted.Count + insufficient + conflicts);
        Assert.InRange(posted.Count, 1, 20);

        var balance = await CashBalanceAsync() ?? -1;
        Assert.Equal(10_000 - (500 * posted.Count), balance);
        Assert.True(balance >= 0);

        // Every successful bet produced exactly one entry, and the balance-after values form an unbroken chain.
        Assert.Equal(posted.Count, await _ledger.QueryAsync(db => db.JournalEntries.CountAsync(e => e.Type == EntryType.BetDebit)));
        var balancesAfter = posted.Select(p => Assert.Single(p.Wallets).BalanceAfter!.Value.Minor).Order().ToList();
        Assert.Equal(Enumerable.Range(0, posted.Count).Select(i => balance + (500L * i)), balancesAfter);

        await AssertLedgerBalancesAsync();
    }

    [Fact]
    public async Task Concurrent_copies_of_one_request_post_exactly_once()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));

        var results = await InParallel(20, _ => _ledger.SendAsync(new PlaceBet("bet-dup", "p-1", Eur(1_000), "round-1")));

        Assert.Single(results.Select(r => r.EntryId).Distinct());
        Assert.Single(results, r => !r.Replayed);
        Assert.All(results, r => Assert.Equal(9_000, Assert.Single(r.Wallets).BalanceAfter!.Value.Minor));
        Assert.Equal(9_000, await CashBalanceAsync());
        await AssertLedgerBalancesAsync();
    }

    [Fact]
    public async Task Concurrent_first_deposits_open_the_wallet_once()
    {
        await InParallel(10, i => _ledger.SendAsync(new SettleDeposit($"dep-{i}", "northwind-pay", "p-new", Eur(100))));

        Assert.Equal(1, await _ledger.QueryAsync(db => db.Accounts.CountAsync(a => a.Code == "player:p-new:cash:EUR")));
        Assert.Equal(1_000, await _ledger.QueryAsync(db => db.Accounts.Where(a => a.Code == "player:p-new:cash:EUR").Select(a => a.BalanceMinor).SingleAsync()));
        await AssertLedgerBalancesAsync();
    }

    private async Task AssertLedgerBalancesAsync()
    {
        // Postings sum to zero per currency, and stored wallet balances match their postings exactly.
        var totals = await _ledger.QueryAsync(db => db.Postings.GroupBy(p => p.Currency).Select(g => g.Sum(p => p.AmountMinor)).ToListAsync());
        Assert.All(totals, total => Assert.Equal(0, total));

        var drift = await _ledger.QueryAsync(db => db.Accounts
            .Where(a => a.BalanceMinor != null)
            .Select(a => new { a.Code, Stored = a.BalanceMinor, Posted = db.Postings.Where(p => p.AccountId == a.Id).Sum(p => p.AmountMinor) })
            .Where(x => x.Stored != x.Posted)
            .ToListAsync());
        Assert.Empty(drift);
    }

    private Task<long?> CashBalanceAsync() =>
        _ledger.QueryAsync(db => db.Accounts.Where(a => a.Code == "player:p-1:cash:EUR").Select(a => a.BalanceMinor).SingleAsync());

    /// <summary>
    /// Starts every call on its own thread-pool thread and releases them together. SQLite I/O completes
    /// synchronously, so without this the "parallel" calls would simply run one after another.
    /// </summary>
    private static async Task<T[]> InParallel<T>(int count, Func<int, Task<T>> call)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await call(i);
        })).ToList();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<object> Outcome(Func<Task<PostingResult>> send)
    {
        try
        {
            return await send();
        }
        catch (LedgerException ex)
        {
            return ex;
        }
    }

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
