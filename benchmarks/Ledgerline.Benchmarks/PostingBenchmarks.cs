using BenchmarkDotNet.Attributes;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Benchmarks;

/// <summary>Pure domain cost of validating and applying a journal entry, with no I/O.</summary>
[MemoryDiagnoser]
public class PostingBenchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly EntryHeader _header = new(EntryType.BetDebit, "wallet:bench", "hash", "bench-1", "p-1") { RoundId = "r-1" };
    private Account _wallet = null!;
    private Account _house = null!;
    private Leg[] _tenLegs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);
        _house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        // Five balanced pairs across ten operator accounts.
        _tenLegs = [.. Enumerable.Range(0, 10).Select(i => new Leg(
            Account.Open(AccountKey.PspClearing($"psp-{i}", Currency.EUR), Now),
            new Money(i % 2 == 0 ? 100 : -100, Currency.EUR)))];
    }

    /// <summary>A win: house to a player wallet (stored balance, version bump, balance-after).</summary>
    [Benchmark(Baseline = true)]
    public JournalEntry TwoLegEntry() =>
        JournalEntry.Post(_header, [new Leg(_house, new Money(-100, Currency.EUR)), new Leg(_wallet, new Money(100, Currency.EUR))], Now);

    [Benchmark]
    public JournalEntry TenLegEntry() => JournalEntry.Post(_header, _tenLegs, Now);
}
