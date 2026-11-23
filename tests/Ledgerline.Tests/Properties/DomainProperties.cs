using CsCheck;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Tests.Properties;

/// <summary>Property-based checks of the domain invariants, generated with CsCheck.</summary>
public sealed class DomainProperties
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly Gen<Currency> AnyCurrency = Gen.OneOf(Currency.All.ToArray());

    private static readonly Gen<long> NonZeroAmount =
        Gen.Long[-1_000_000_000, 1_000_000_000].Where(x => x != 0);

    [Fact]
    public void Every_posted_entry_balances_to_zero_per_currency()
    {
        // n-1 random legs plus one leg that closes the entry, spread over up to two currencies.
        var gen = Gen.Select(NonZeroAmount.Array[1, 8], NonZeroAmount.Array[1, 8], AnyCurrency, AnyCurrency);

        gen.Sample((first, second, c1, c2) =>
        {
            var legs = BalancedLegs(first, c1).Concat(BalancedLegs(second, c2)).ToList();
            if (legs.Count < 2)
            {
                return;
            }

            var entry = JournalEntry.Post(Header(), legs, Now);

            foreach (var group in entry.Postings.GroupBy(p => p.Currency))
            {
                Assert.Equal(0, group.Sum(p => p.AmountMinor));
            }

            Assert.Equal(legs.Count, entry.Postings.Count);
        });
    }

    [Fact]
    public void Any_entry_that_does_not_balance_is_rejected()
    {
        var gen = Gen.Select(NonZeroAmount.Array[1, 8], NonZeroAmount, AnyCurrency);

        gen.Sample((amounts, skew, currency) =>
        {
            var legs = BalancedLegs(amounts, currency).ToList();
            if (legs.Count < 2)
            {
                return;
            }

            // Skew one leg; if that zeroes it, the zero-amount rule rejects it instead.
            var skewed = legs[0] with { Amount = new Money(legs[0].Amount.Minor + skew, currency) };
            legs[0] = skewed;

            var ex = Record.Exception(() => JournalEntry.Post(Header(), legs, Now));
            Assert.True(ex is UnbalancedEntryException or LedgerValidationException, ex?.GetType().Name);
        });
    }

    [Fact]
    public void Wallets_never_go_negative_whatever_the_sequence_of_movements()
    {
        var gen = Gen.Long[-5_000, 5_000].Where(x => x != 0).Array[1, 60];

        gen.Sample(movements =>
        {
            var wallet = Account.Open(AccountKey.PlayerCash("p-1", Currency.EUR), Now);
            var house = Account.Open(AccountKey.HouseGaming(Currency.EUR), Now);
            long expected = 0;

            foreach (var delta in movements)
            {
                try
                {
                    JournalEntry.Post(Header(), [new Leg(wallet, Eur(delta)), new Leg(house, Eur(-delta))], Now);
                    expected += delta;
                }
                catch (InsufficientFundsException)
                {
                    Assert.True(expected + delta < 0);
                }

                Assert.True(wallet.BalanceMinor >= 0);
            }

            Assert.Equal(expected, wallet.BalanceMinor);
        });
    }

    [Fact]
    public void Major_and_minor_units_round_trip_exactly()
    {
        Gen.Select(Gen.Long[-999_999_999_999_999, 999_999_999_999_999], AnyCurrency).Sample((minor, currency) =>
        {
            var money = new Money(minor, currency);
            Assert.Equal(money, Money.FromMajor(money.ToMajor(), currency));
        });
    }

    /// <summary>One operator account per amount plus a closing leg, so the set sums to zero.</summary>
    private static IEnumerable<Leg> BalancedLegs(long[] amounts, Currency currency)
    {
        var closing = -amounts.Sum();
        long[] all = closing == 0 ? amounts : [.. amounts, closing];
        if (all.Length < 2)
        {
            yield break;
        }

        for (var i = 0; i < all.Length; i++)
        {
            yield return new Leg(Account.Open(AccountKey.PspClearing($"psp-{i}", currency), Now), new Money(all[i], currency));
        }
    }

    private static EntryHeader Header() => new(EntryType.WalletTransfer, $"test:{Guid.NewGuid()}", "hash", "tx-1", "p-1");

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
