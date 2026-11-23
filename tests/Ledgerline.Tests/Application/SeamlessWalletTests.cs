using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Domain.Journal;
using Ledgerline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Tests.Application;

/// <summary>Bet, win and rollback through the real dispatcher, EF Core store and SQLite.</summary>
public sealed class SeamlessWalletTests : IAsyncLifetime
{
    private LedgerServices _ledger = null!;

    public async ValueTask InitializeAsync()
    {
        _ledger = await LedgerServices.CreateAsync();
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
    }

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task A_bet_then_a_win_move_the_cash_wallet_and_the_house()
    {
        var bet = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));
        var win = await _ledger.SendAsync(new CreditWin("win-1", "p-1", Eur(6_000), "round-1"));

        Assert.Equal(EntryType.BetDebit, bet.Type);
        Assert.Equal(7_500, Assert.Single(bet.Wallets).BalanceAfter!.Value.Minor);
        Assert.Equal(13_500, Assert.Single(win.Wallets).BalanceAfter!.Value.Minor);
        Assert.Equal(-3_500, await HouseBalanceAsync());
    }

    [Fact]
    public async Task A_replay_returns_the_original_result_and_posts_nothing()
    {
        var first = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));
        await _ledger.SendAsync(new CreditWin("win-1", "p-1", Eur(1_000), "round-1"));

        var replay = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.EntryId, replay.EntryId);
        Assert.Equal(first.PostedAt, replay.PostedAt);

        // The balance in the replay is the balance right after the original bet, not today's.
        Assert.Equal(first.Postings, replay.Postings);
        Assert.Equal(7_500, Assert.Single(replay.Wallets).BalanceAfter!.Value.Minor);
        Assert.Equal(3, await _ledger.QueryAsync(db => db.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Reusing_a_transaction_id_for_a_different_request_is_rejected()
    {
        await _ledger.SendAsync(new PlaceBet("tx-1", "p-1", Eur(2_500), "round-1"));

        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => _ledger.SendAsync(new PlaceBet("tx-1", "p-1", Eur(2_600), "round-1")));

        // Bets and wins share one id space: a win cannot reuse a bet's id, even with identical fields.
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => _ledger.SendAsync(new CreditWin("tx-1", "p-1", Eur(2_500), "round-1")));
    }

    [Fact]
    public async Task A_bet_larger_than_the_balance_is_rejected_and_leaves_no_trace()
    {
        var ex = await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _ledger.SendAsync(new PlaceBet("bet-big", "p-1", Eur(10_001), "round-1")));

        Assert.Equal(10_000, ex.Available.Minor);
        Assert.Equal(1, await _ledger.QueryAsync(db => db.JournalEntries.CountAsync()));
        Assert.Equal(10_000, await CashBalanceAsync("p-1"));
    }

    [Fact]
    public async Task A_rejected_bet_can_be_retried_with_the_same_id_once_funds_arrive()
    {
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _ledger.SendAsync(new PlaceBet("bet-big", "p-1", Eur(15_000), "round-1")));
        await _ledger.SendAsync(new SettleDeposit("dep-2", "northwind-pay", "p-1", Eur(5_000)));

        var retried = await _ledger.SendAsync(new PlaceBet("bet-big", "p-1", Eur(15_000), "round-1"));

        Assert.False(retried.Replayed);
        Assert.Equal(0, await CashBalanceAsync("p-1"));
    }

    [Fact]
    public async Task Rolling_back_a_bet_posts_its_mirror_image_exactly_once()
    {
        var bet = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        var rollback = await _ledger.SendAsync(new RollbackBet("rb-1", "p-1", "bet-1"));

        Assert.Equal(EntryType.BetRollback, rollback.Type);
        Assert.Equal(bet.Postings.Select(p => (p.AccountId, -p.Amount.Minor)), rollback.Postings.Select(p => (p.AccountId, p.Amount.Minor)));
        Assert.Equal(10_000, await CashBalanceAsync("p-1"));
        Assert.Equal(0, await HouseBalanceAsync());

        var replay = await _ledger.SendAsync(new RollbackBet("rb-1", "p-1", "bet-1"));
        Assert.True(replay.Replayed);

        var ex = await Assert.ThrowsAsync<InvalidLedgerStateException>(
            () => _ledger.SendAsync(new RollbackBet("rb-2", "p-1", "bet-1")));
        Assert.Contains("already been rolled back", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollbacks_only_find_the_callers_own_bets()
    {
        await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        await Assert.ThrowsAsync<LedgerNotFoundException>(() => _ledger.SendAsync(new RollbackBet("rb-1", "p-1", "missing")));
        await Assert.ThrowsAsync<LedgerNotFoundException>(() => _ledger.SendAsync(new RollbackBet("rb-2", "p-2", "bet-1")));
    }

    [Fact]
    public async Task Only_bets_can_be_rolled_back()
    {
        await _ledger.SendAsync(new CreditWin("win-1", "p-1", Eur(500), "round-1"));

        await Assert.ThrowsAsync<InvalidLedgerStateException>(() => _ledger.SendAsync(new RollbackBet("rb-1", "p-1", "win-1")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Amounts_must_be_positive(long minor)
    {
        var ex = await Assert.ThrowsAsync<LedgerValidationException>(
            () => _ledger.SendAsync(new PlaceBet("bet-x", "p-1", Eur(minor), "round-1")));

        Assert.Equal("amountMinor", ex.Field);
    }

    private Task<long?> CashBalanceAsync(string playerId) =>
        _ledger.QueryAsync(db => db.Accounts.Where(a => a.Code == $"player:{playerId}:cash:EUR").Select(a => a.BalanceMinor).SingleAsync());

    private Task<long> HouseBalanceAsync() =>
        _ledger.QueryAsync(db => db.Postings
            .Where(p => db.Accounts.Any(a => a.Id == p.AccountId && a.Code == "house:gaming:EUR"))
            .SumAsync(p => p.AmountMinor));

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
