using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Tests.Domain;

public sealed class JournalEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly EntryHeader Header = new(EntryType.BetDebit, "wallet:tx-1", "hash", "tx-1", "p-1") { RoundId = "round-1" };

    [Fact]
    public void A_balanced_entry_moves_money_and_records_balances_after()
    {
        var cash = Funded(1000);
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        var entry = JournalEntry.Post(Header, [new Leg(cash, Eur(-250)), new Leg(house, Eur(250))], Now);

        Assert.Equal(EntryType.BetDebit, entry.Type);
        Assert.Equal("round-1", entry.RoundId);
        Assert.Equal(750, cash.BalanceMinor);
        Assert.Collection(
            entry.Postings,
            p =>
            {
                Assert.Equal(cash.Id, p.AccountId);
                Assert.Equal(-250, p.AmountMinor);
                Assert.Equal(750, p.BalanceAfterMinor);
            },
            p =>
            {
                Assert.Equal(house.Id, p.AccountId);
                Assert.Equal(250, p.AmountMinor);
                Assert.Null(p.BalanceAfterMinor);
            });
        Assert.All(entry.Postings, p => Assert.Equal(entry.Id, p.EntryId));
        Assert.Equal(0, entry.Postings.Sum(p => p.AmountMinor));
    }

    [Fact]
    public void Posting_raises_one_event_describing_every_leg()
    {
        var cash = Funded(1000);
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        var entry = JournalEntry.Post(Header, [new Leg(cash, Eur(-250)), new Leg(house, Eur(250))], Now);

        var posted = Assert.IsType<JournalEntryPosted>(Assert.Single(entry.DomainEvents));
        Assert.Equal(entry.Id, posted.EntryId);
        Assert.Equal("p-1", posted.PlayerId);
        Assert.Equal(new[] { "player:p-1:cash:EUR", "house:gaming:EUR" }, posted.Legs.Select(l => l.AccountCode));
        Assert.Equal(new long?[] { 750, null }, posted.Legs.Select(l => l.BalanceAfterMinor));
    }

    [Fact]
    public void Unbalanced_entries_are_rejected()
    {
        var cash = Funded(1000);
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        var ex = Assert.Throws<UnbalancedEntryException>(
            () => JournalEntry.Post(Header, [new Leg(cash, Eur(-250)), new Leg(house, Eur(249))], Now));

        Assert.Contains("sum to -1", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1000, cash.BalanceMinor);
    }

    [Fact]
    public void Each_currency_must_balance_on_its_own()
    {
        var eurHouse = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);
        var eurPool = Account.Open(AccountKey.BonusPool(Currency.EUR), Now);
        var usdHouse = Account.Open(AccountKey.HouseGaming(Currency.USD), Now);
        var usdPool = Account.Open(AccountKey.BonusPool(Currency.USD), Now);

        var balanced = JournalEntry.Post(
            Header,
            [new Leg(eurHouse, Eur(-100)), new Leg(eurPool, Eur(100)), new Leg(usdHouse, Usd(-7)), new Leg(usdPool, Usd(7))],
            Now);
        Assert.Equal(4, balanced.Postings.Count);

        // EUR -100 and USD +100 would "sum to zero" if currencies were ignored.
        Assert.Throws<UnbalancedEntryException>(
            () => JournalEntry.Post(Header, [new Leg(eurHouse, Eur(-100)), new Leg(usdHouse, Usd(100))], Now));
    }

    [Fact]
    public void An_entry_needs_at_least_two_postings()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Post(Header, [new Leg(house, Eur(0))], Now));
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Post(Header, [], Now));
    }

    [Fact]
    public void Zero_amount_postings_are_rejected()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);
        var pool = Account.Open(AccountKey.BonusPool(Currency.EUR), Now);

        var ex = Assert.Throws<LedgerValidationException>(
            () => JournalEntry.Post(Header, [new Leg(house, Eur(0)), new Leg(pool, Eur(0))], Now));
        Assert.Equal("amount", ex.Field);
    }

    [Fact]
    public void The_same_account_cannot_appear_twice()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);

        Assert.Throws<UnbalancedEntryException>(
            () => JournalEntry.Post(Header, [new Leg(house, Eur(100)), new Leg(house, Eur(-100))], Now));
    }

    [Fact]
    public void A_leg_in_a_different_currency_than_its_account_is_rejected()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);
        var pool = Account.Open(AccountKey.BonusPool(Currency.USD), Now);

        Assert.Throws<CurrencyMismatchException>(
            () => JournalEntry.Post(Header, [new Leg(house, Eur(-100)), new Leg(pool, Eur(100))], Now));
    }

    [Fact]
    public void A_rejected_entry_leaves_every_account_untouched()
    {
        // Moving 500 out of a wallet holding 200 into another wallet: the credit leg would succeed
        // on its own, but nothing may be applied because the debit leg fails.
        var cash = Funded(200);
        var bonus = Account.Open(AccountKey.PlayerBonus("p-1", Currency.EUR), Now);

        Assert.Throws<InsufficientFundsException>(
            () => JournalEntry.Post(Header, [new Leg(bonus, Eur(500)), new Leg(cash, Eur(-500))], Now));

        Assert.Equal(200, cash.BalanceMinor);
        Assert.Equal(0, bonus.BalanceMinor);
        Assert.Equal(0, bonus.Version);
    }

    [Fact]
    public void Identifiers_are_validated()
    {
        var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);
        var pool = Account.Open(AccountKey.BonusPool(Currency.EUR), Now);

        var ex = Assert.Throws<LedgerValidationException>(
            () => JournalEntry.Post(Header with { TransactionId = "tx 1" }, [new Leg(house, Eur(-1)), new Leg(pool, Eur(1))], Now));
        Assert.Equal("transactionId", ex.Field);
    }

    private static Account Funded(long minor)
    {
        var cash = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);
        cash.Apply(Eur(minor));
        return cash;
    }

    private static Money Eur(long minor) => new(minor, Currency.EUR);

    private static Money Usd(long minor) => new(minor, Currency.USD);
}
