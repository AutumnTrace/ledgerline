using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;

namespace Ledgerline.Tests.Domain;

public sealed class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Account_codes_are_stable_and_readable()
    {
        Assert.Equal("player:p-1001:cash:EUR", AccountKey.PlayerCash("p-1001", Currency.EUR).Code);
        Assert.Equal("player:p-1001:bonus:EUR", AccountKey.PlayerBonus("p-1001", Currency.EUR).Code);
        Assert.Equal("house:gaming:JPY", AccountKey.HouseGaming(Currency.JPY).Code);
        Assert.Equal("house:bonus-pool:EUR", AccountKey.BonusPool(Currency.EUR).Code);
        Assert.Equal("psp:northwind:clearing:USD", AccountKey.PspClearing("northwind", Currency.USD).Code);
        Assert.Equal("psp:northwind:suspense:USD", AccountKey.PspSuspense("northwind", Currency.USD).Code);
    }

    [Theory]
    [InlineData("p:1001")]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    public void Player_ids_that_could_break_account_codes_are_rejected(string playerId)
    {
        var ex = Assert.Throws<LedgerValidationException>(() => AccountKey.PlayerCash(playerId, Currency.EUR));
        Assert.Equal("playerId", ex.Field);
    }

    [Fact]
    public void Player_wallets_open_with_a_zero_stored_balance()
    {
        var wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);

        Assert.True(wallet.TracksBalance);
        Assert.Equal(0, wallet.BalanceMinor);
        Assert.Equal(0, wallet.Version);
        Assert.Equal(Now, wallet.OpenedAt);
    }

    [Fact]
    public void Operator_accounts_store_no_balance()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        Assert.False(house.TracksBalance);
        Assert.Null(house.BalanceMinor);
        Assert.Throws<InvalidOperationException>(() => house.Balance);
    }

    [Fact]
    public void Applying_to_a_wallet_moves_the_balance_and_bumps_the_version()
    {
        var wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);

        Assert.Equal(1000, wallet.Apply(new Money(1000, Currency.EUR)));
        Assert.Equal(750, wallet.Apply(new Money(-250, Currency.EUR)));
        Assert.Equal(2, wallet.Version);
    }

    [Fact]
    public void A_wallet_can_never_go_negative()
    {
        var wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);
        wallet.Apply(new Money(100, Currency.EUR));

        var ex = Assert.Throws<InsufficientFundsException>(() => wallet.Apply(new Money(-101, Currency.EUR)));

        Assert.Equal(new Money(100, Currency.EUR), ex.Available);
        Assert.Equal(new Money(101, Currency.EUR), ex.Requested);
        Assert.Equal(100, wallet.BalanceMinor);
        Assert.Equal(1, wallet.Version);
    }

    [Fact]
    public void Operator_accounts_accept_any_amount_without_changing_state()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        Assert.Null(house.Apply(new Money(-1_000_000, Currency.EUR)));
        Assert.Equal(0, house.Version);
    }

    [Fact]
    public void Amounts_in_another_currency_are_rejected()
    {
        var wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);

        Assert.Throws<CurrencyMismatchException>(() => wallet.Apply(new Money(100, Currency.USD)));
    }
}
