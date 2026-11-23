using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;
using Ledgerline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Tests.Application;

/// <summary>Deposits, the withdrawal hold lifecycle, bonuses and wallet transfers.</summary>
public sealed class PaymentCommandTests : IAsyncLifetime
{
    private LedgerServices _ledger = null!;

    public async ValueTask InitializeAsync() => _ledger = await LedgerServices.CreateAsync();

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task A_deposit_opens_the_wallet_on_first_use_and_is_idempotent_per_provider()
    {
        var first = await _ledger.SendAsync(new SettleDeposit("psp-tx-1", "northwind-pay", "p-1", Eur(5_000)));
        var replay = await _ledger.SendAsync(new SettleDeposit("psp-tx-1", "northwind-pay", "p-1", Eur(5_000)));

        // Same transaction id from another provider is a different deposit.
        var other = await _ledger.SendAsync(new SettleDeposit("psp-tx-1", "southgate-pay", "p-1", Eur(1_000)));

        Assert.True(replay.Replayed);
        Assert.Equal(first.EntryId, replay.EntryId);
        Assert.False(other.Replayed);
        Assert.Equal(6_000, await BalanceAsync("player:p-1:cash:EUR"));
        Assert.Equal(-5_000, await BalanceAsync("psp:northwind-pay:clearing:EUR"));
    }

    [Fact]
    public async Task A_withdrawal_holds_the_money_until_the_provider_completes_it()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));

        var reserved = await _ledger.SendAsync(new RequestWithdrawal("wd-1", "northwind-pay", "p-1", Eur(4_000)));
        Assert.Equal(EntryType.WithdrawalReserved, reserved.Type);
        Assert.Equal(6_000, await BalanceAsync("player:p-1:cash:EUR"));
        Assert.Equal(4_000, await BalanceAsync("psp:northwind-pay:suspense:EUR"));
        Assert.Equal(WithdrawalStatus.Reserved, (await WithdrawalAsync("wd-1")).Status);

        // The held money cannot be spent twice.
        await Assert.ThrowsAsync<InsufficientFundsException>(() => _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(6_001), "r-1")));

        var completed = await _ledger.SendAsync(new CompleteWithdrawal("wd-1"));
        Assert.Equal(EntryType.WithdrawalCompleted, completed.Type);
        Assert.Empty(completed.Wallets);
        Assert.Equal(0, await BalanceAsync("psp:northwind-pay:suspense:EUR"));
        Assert.Equal(-6_000, await BalanceAsync("psp:northwind-pay:clearing:EUR"));

        var withdrawal = await WithdrawalAsync("wd-1");
        Assert.Equal(WithdrawalStatus.Completed, withdrawal.Status);
        Assert.Equal(completed.EntryId, withdrawal.ResolutionEntryId);

        Assert.True((await _ledger.SendAsync(new CompleteWithdrawal("wd-1"))).Replayed);
        await Assert.ThrowsAsync<InvalidLedgerStateException>(() => _ledger.SendAsync(new ReverseWithdrawal("wd-1", "too late")));
    }

    [Fact]
    public async Task A_reversed_withdrawal_returns_the_hold_to_the_player()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        await _ledger.SendAsync(new RequestWithdrawal("wd-1", "northwind-pay", "p-1", Eur(4_000)));

        var reversed = await _ledger.SendAsync(new ReverseWithdrawal("wd-1", "Provider declined the payout"));

        Assert.Equal(10_000, Assert.Single(reversed.Wallets).BalanceAfter!.Value.Minor);
        Assert.Equal(0, await BalanceAsync("psp:northwind-pay:suspense:EUR"));
        var withdrawal = await WithdrawalAsync("wd-1");
        Assert.Equal(WithdrawalStatus.Reversed, withdrawal.Status);
        Assert.Equal("Provider declined the payout", withdrawal.ReversalReason);
        await Assert.ThrowsAsync<InvalidLedgerStateException>(() => _ledger.SendAsync(new CompleteWithdrawal("wd-1")));
    }

    [Fact]
    public async Task A_withdrawal_larger_than_the_balance_reserves_nothing()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(1_000)));

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _ledger.SendAsync(new RequestWithdrawal("wd-1", "northwind-pay", "p-1", Eur(1_001))));

        Assert.False(await _ledger.QueryAsync(db => db.Withdrawals.AnyAsync()));
        await Assert.ThrowsAsync<LedgerNotFoundException>(() => _ledger.SendAsync(new CompleteWithdrawal("wd-1")));
    }

    [Fact]
    public async Task Bonus_money_can_be_released_into_cash()
    {
        await _ledger.SendAsync(new GrantBonus("bonus-1", "p-1", Eur(2_000)));

        var transfer = await _ledger.SendAsync(new TransferBetweenWallets("tr-1", "p-1", Wallet.Bonus, Wallet.Cash, Eur(1_500)));

        Assert.Equal(
            new[] { ("player:p-1:bonus:EUR", 500L), ("player:p-1:cash:EUR", 1_500L) },
            transfer.Wallets.Select(w => (w.AccountCode, w.BalanceAfter!.Value.Minor)));
        Assert.Equal(-2_000, await BalanceAsync("house:bonus-pool:EUR"));
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _ledger.SendAsync(new TransferBetweenWallets("tr-2", "p-1", Wallet.Bonus, Wallet.Cash, Eur(501))));
    }

    [Fact]
    public async Task A_transfer_needs_two_different_wallets()
    {
        await Assert.ThrowsAsync<LedgerValidationException>(
            () => _ledger.SendAsync(new TransferBetweenWallets("tr-1", "p-1", Wallet.Cash, Wallet.Cash, Eur(1))));
    }

    [Fact]
    public async Task Currencies_get_separate_wallets()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-eur", "northwind-pay", "p-1", Eur(1_000)));
        await _ledger.SendAsync(new SettleDeposit("dep-jpy", "northwind-pay", "p-1", new Money(5_000, Currency.JPY)));

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _ledger.SendAsync(new PlaceBet("bet-1", "p-1", new Money(1_001, Currency.EUR), "r-1")));
        Assert.Equal(5_000, await BalanceAsync("player:p-1:cash:JPY"));
    }

    /// <summary>Stored balance for wallets, sum of postings for operator accounts.</summary>
    private Task<long> BalanceAsync(string code) =>
        _ledger.QueryAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Code == code);
            return account.BalanceMinor ?? await db.Postings.Where(p => p.AccountId == account.Id).SumAsync(p => p.AmountMinor);
        });

    private Task<Withdrawal> WithdrawalAsync(string withdrawalId) =>
        _ledger.QueryAsync(db => db.Withdrawals.SingleAsync(w => w.WithdrawalId == withdrawalId));

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
