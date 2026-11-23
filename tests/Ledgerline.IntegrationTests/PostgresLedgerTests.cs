using System.Diagnostics.Metrics;
using Ledgerline.Application;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;
using Ledgerline.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledgerline.IntegrationTests;

/// <summary>
/// The write side on PostgreSQL 18: migrations, real row locks under READ COMMITTED, and the
/// optimistic-concurrency retry path with many writers on one wallet.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresLedgerTests(ContainersFixture containers) : IAsyncLifetime
{
    private PostgresLedger _ledger = null!;

    public async ValueTask InitializeAsync() => _ledger = await PostgresLedger.CreateAsync(containers);

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task Migrations_create_the_indexes_the_queries_rely_on()
    {
        var indexes = new Dictionary<string, string>();
        await using (var connection = new NpgsqlConnection(_ledger.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public'", connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                indexes[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Assert.Contains("INCLUDE (entry_id, entry_type, amount_minor, balance_after_minor, posted_at)", indexes["ix_postings_account_statement"], StringComparison.Ordinal);
        Assert.Contains("WHERE ((status)::text = 'Reserved'::text)", indexes["ix_withdrawals_pending"], StringComparison.Ordinal);
        Assert.Contains("WHERE (processed_at IS NULL)", indexes["ix_outbox_messages_pending"], StringComparison.Ordinal);
        Assert.Contains("WHERE (reverses_entry_id IS NOT NULL)", indexes["ix_journal_entries_reverses_entry_id"], StringComparison.Ordinal);
        Assert.StartsWith("CREATE UNIQUE INDEX", indexes["ix_journal_entries_idempotency_key"], StringComparison.Ordinal);
        Assert.StartsWith("CREATE UNIQUE INDEX", indexes["ix_accounts_code"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_hundred_parallel_bets_never_overdraw_a_wallet()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        long retries = 0;
        using var listener = CountRetries(v => Interlocked.Add(ref retries, v));

        // 100 bets of 5.00 against 100.00: at most 20 can ever succeed.
        var outcomes = await InParallel(100, i => Outcome(() => _ledger.SendAsync(new PlaceBet($"bet-{i}", "p-1", Eur(500), "round-1"))));

        var posted = outcomes.OfType<PostingResult>().ToList();
        var insufficient = outcomes.OfType<InsufficientFundsException>().Count();
        var gaveUp = outcomes.OfType<WriteConflictException>().Count();
        TestContext.Current.TestOutputHelper?.WriteLine($"posted={posted.Count} insufficient={insufficient} gave-up={gaveUp} conflict-retries={retries}");

        Assert.Equal(100, posted.Count + insufficient + gaveUp);
        Assert.InRange(posted.Count, 1, 20);
        var balance = await CashBalanceAsync("p-1");
        Assert.Equal(10_000 - (500L * posted.Count), balance);
        Assert.Equal(
            Enumerable.Range(0, posted.Count).Select(i => balance + (500L * i)),
            posted.Select(p => Assert.Single(p.Wallets).BalanceAfter!.Value.Minor).Order());
        await AssertBooksBalanceAsync();
    }

    [Fact]
    public async Task Concurrent_copies_of_one_request_post_exactly_once()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));

        var results = await InParallel(30, _ => _ledger.SendAsync(new PlaceBet("bet-dup", "p-1", Eur(1_000), "round-1")));

        Assert.Single(results.Select(r => r.EntryId).Distinct());
        Assert.Single(results, r => !r.Replayed);
        Assert.All(results, r => Assert.Equal(results[0].PostedAt, r.PostedAt));
        Assert.Equal(9_000, await CashBalanceAsync("p-1"));
        Assert.Equal(1, await _ledger.QueryAsync(db => db.JournalEntries.CountAsync(e => e.IdempotencyKey == "wallet:bet-dup")));
    }

    [Fact]
    public async Task A_racing_complete_and_reverse_resolve_a_withdrawal_exactly_once()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        await _ledger.SendAsync(new RequestWithdrawal("wd-1", "northwind-pay", "p-1", Eur(4_000)));

        var outcomes = await InParallel(20, i => Outcome(() => i % 2 == 0
            ? _ledger.SendAsync(new CompleteWithdrawal("wd-1"))
            : _ledger.SendAsync(new ReverseWithdrawal("wd-1", "declined"))));

        var resolutions = outcomes.OfType<PostingResult>().Where(r => !r.Replayed).ToList();
        var winner = Assert.Single(resolutions);
        Assert.All(outcomes, o => Assert.True(o is PostingResult or InvalidLedgerStateException or WriteConflictException, o.GetType().Name));

        var withdrawal = await _ledger.QueryAsync(db => db.Withdrawals.SingleAsync(w => w.WithdrawalId == "wd-1"));
        Assert.Equal(winner.EntryId, withdrawal.ResolutionEntryId);
        Assert.Equal(winner.Type == EntryType.WithdrawalCompleted ? WithdrawalStatus.Completed : WithdrawalStatus.Reversed, withdrawal.Status);
        Assert.Equal(winner.Type == EntryType.WithdrawalCompleted ? 6_000 : 10_000, await CashBalanceAsync("p-1"));
        await AssertBooksBalanceAsync();
    }

    [Fact]
    public async Task A_random_parallel_workload_keeps_the_books_balanced()
    {
        var random = new Random(20261123);
        var players = Enumerable.Range(1, 8).Select(i => $"p-{i}").ToArray();
        var currencies = new[] { Currency.EUR, Currency.JPY };
        var operations = Enumerable.Range(0, 600).Select(i =>
        {
            var player = players[random.Next(players.Length)];
            var currency = currencies[random.Next(currencies.Length)];
            var amount = new Money(random.Next(1, 5_000), currency);
            return random.Next(6) switch
            {
                0 or 1 => (ICommand)new SettleDeposit($"dep-{i}", "northwind-pay", player, amount),
                2 => new PlaceBet($"bet-{i}", player, amount, $"r-{i % 40}"),
                3 => new CreditWin($"win-{i}", player, amount, $"r-{i % 40}"),
                4 => new RequestWithdrawal($"wd-{i}", "northwind-pay", player, amount),
                _ => new TransferBetweenWallets($"tr-{i}", player, Wallet.Cash, Wallet.Bonus, amount),
            };
        }).ToList();

        var outcomes = await InParallel(operations.Count, i => Outcome(() => Send(operations[i])));

        Assert.DoesNotContain(outcomes, o => o is Exception and not (InsufficientFundsException or WriteConflictException));
        var posted = outcomes.OfType<PostingResult>().Count();
        Assert.True(posted > 100, $"only {posted} operations posted");
        Assert.Equal(posted, await _ledger.QueryAsync(db => db.JournalEntries.CountAsync()));
        await AssertBooksBalanceAsync();

        // Money in suspense equals the open withdrawals, per currency.
        var held = await _ledger.QueryAsync(db => db.Withdrawals.Where(w => w.Status == WithdrawalStatus.Reserved)
            .GroupBy(w => w.Currency).Select(g => new { Currency = g.Key, Total = g.Sum(w => w.AmountMinor) }).ToListAsync());
        foreach (var currency in held)
        {
            Assert.Equal(currency.Total, await DerivedBalanceAsync($"psp:northwind-pay:suspense:{currency.Currency.Code}"));
        }
    }

    [Fact]
    public async Task A_replay_read_back_from_PostgreSQL_is_identical_to_the_original()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        var original = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        var replay = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        Assert.True(replay.Replayed);
        Assert.Equal(original with { Replayed = true, Postings = replay.Postings }, replay);
        Assert.Equal(original.Postings, replay.Postings);
    }

    private Task<PostingResult> Send(ICommand command) => command switch
    {
        SettleDeposit c => _ledger.SendAsync(c),
        PlaceBet c => _ledger.SendAsync(c),
        CreditWin c => _ledger.SendAsync(c),
        RequestWithdrawal c => _ledger.SendAsync(c),
        TransferBetweenWallets c => _ledger.SendAsync(c),
        _ => throw new InvalidOperationException(command.GetType().Name),
    };

    /// <summary>Postings net to zero per currency; stored wallet balances equal their postings and are never negative.</summary>
    private async Task AssertBooksBalanceAsync()
    {
        var totals = await _ledger.QueryAsync(db => db.Postings.GroupBy(p => p.Currency).Select(g => g.Sum(p => p.AmountMinor)).ToListAsync());
        Assert.All(totals, total => Assert.Equal(0, total));

        var drift = await _ledger.QueryAsync(db => db.Accounts
            .Where(a => a.BalanceMinor != null)
            .Select(a => new { a.Code, Stored = a.BalanceMinor, Posted = db.Postings.Where(p => p.AccountId == a.Id).Sum(p => p.AmountMinor) })
            .Where(x => x.Stored != x.Posted || x.Stored < 0)
            .ToListAsync());
        Assert.Empty(drift);
    }

    private Task<long> CashBalanceAsync(string playerId) =>
        _ledger.QueryAsync(db => db.Accounts.Where(a => a.Code == $"player:{playerId}:cash:EUR").Select(a => a.BalanceMinor!.Value).SingleAsync());

    private Task<long> DerivedBalanceAsync(string code) =>
        _ledger.QueryAsync(db => db.Postings.Where(p => db.Accounts.Any(a => a.Id == p.AccountId && a.Code == code)).SumAsync(p => p.AmountMinor));

    private static MeterListener CountRetries(Action<long> onRetry)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "ledger.concurrency.retries")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => onRetry(value));
        listener.Start();
        return listener;
    }

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
