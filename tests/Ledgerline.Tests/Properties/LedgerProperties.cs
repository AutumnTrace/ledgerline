using CsCheck;
using Ledgerline.Application;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Withdrawals;
using Ledgerline.Tests.Support;

namespace Ledgerline.Tests.Properties;

/// <summary>
/// Whole-ledger invariants over random sequences of real commands (deposits, bets, wins, rollbacks,
/// withdrawals, bonuses, transfers and replays), run through the real handlers on an in-memory store.
/// </summary>
public sealed class LedgerProperties
{
    private static readonly string[] Players = ["p-1", "p-2", "p-3"];
    private static readonly Currency[] Currencies = [Currency.EUR, Currency.JPY];

    private static readonly Gen<Op> AnyOp = Gen.Frequency(
        (4, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 20_000]).Select(t => (Op)new DepositOp(t.Item1, t.Item2, t.Item3))),
        (5, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 8_000]).Select(t => (Op)new BetOp(t.Item1, t.Item2, t.Item3))),
        (3, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 15_000]).Select(t => (Op)new WinOp(t.Item1, t.Item2, t.Item3))),
        (2, Gen.Int[0, 50].Select(i => (Op)new RollbackOp(i))),
        (2, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 10_000]).Select(t => (Op)new WithdrawOp(t.Item1, t.Item2, t.Item3))),
        (2, Gen.Select(Gen.Int[0, 50], Gen.Bool).Select(t => (Op)new ResolveWithdrawalOp(t.Item1, t.Item2))),
        (1, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 5_000]).Select(t => (Op)new BonusOp(t.Item1, t.Item2, t.Item3))),
        (1, Gen.Select(Gen.Int[0, 2], Gen.Int[0, 1], Gen.Long[1, 5_000], Gen.Bool).Select(t => (Op)new TransferOp(t.Item1, t.Item2, t.Item3, t.Item4))),
        (2, Gen.Int[0, 50].Select(i => (Op)new ReplayOp(i))));

    [Fact]
    public async Task The_ledger_always_balances_and_wallets_never_go_negative()
    {
        await AnyOp.List[1, 120].SampleAsync(async ops =>
        {
            var run = new LedgerRun();
            foreach (var op in ops)
            {
                await run.ApplyAsync(op);
            }

            run.AssertInvariants();
        }, iter: 300);
    }

    private abstract record Op;

    private sealed record DepositOp(int Player, int Currency, long Amount) : Op;

    private sealed record BetOp(int Player, int Currency, long Amount) : Op;

    private sealed record WinOp(int Player, int Currency, long Amount) : Op;

    private sealed record RollbackOp(int BetIndex) : Op;

    private sealed record WithdrawOp(int Player, int Currency, long Amount) : Op;

    private sealed record ResolveWithdrawalOp(int Index, bool Complete) : Op;

    private sealed record BonusOp(int Player, int Currency, long Amount) : Op;

    private sealed record TransferOp(int Player, int Currency, long Amount, bool BonusToCash) : Op;

    private sealed record ReplayOp(int Index) : Op;

    /// <summary>One random history, executed command by command against a fresh in-memory ledger.</summary>
    private sealed class LedgerRun
    {
        private readonly InMemoryLedgerStore _store = new(TimeProvider.System);
        private readonly List<(ICommand Command, PostingResult Result)> _successes = [];
        private readonly List<PlaceBet> _bets = [];
        private readonly List<string> _withdrawals = [];
        private int _sequence;

        public async Task ApplyAsync(Op op)
        {
            if (op is ReplayOp replay)
            {
                await ReplayAsync(replay.Index);
                return;
            }

            ICommand? command = op switch
            {
                DepositOp o => new SettleDeposit(NextId("dep"), "northwind-pay", Players[o.Player], Money(o.Amount, o.Currency)),
                BetOp o => new PlaceBet(NextId("bet"), Players[o.Player], Money(o.Amount, o.Currency), "round-1"),
                WinOp o => new CreditWin(NextId("win"), Players[o.Player], Money(o.Amount, o.Currency), "round-1"),
                RollbackOp o when _bets.Count > 0 => Rollback(_bets[o.BetIndex % _bets.Count]),
                WithdrawOp o => new RequestWithdrawal(NextId("wd"), "northwind-pay", Players[o.Player], Money(o.Amount, o.Currency)),
                ResolveWithdrawalOp o when _withdrawals.Count > 0 => o.Complete
                    ? new CompleteWithdrawal(_withdrawals[o.Index % _withdrawals.Count])
                    : new ReverseWithdrawal(_withdrawals[o.Index % _withdrawals.Count], "declined"),
                BonusOp o => new GrantBonus(NextId("bonus"), Players[o.Player], Money(o.Amount, o.Currency)),
                TransferOp o => new TransferBetweenWallets(
                    NextId("tr"),
                    Players[o.Player],
                    o.BonusToCash ? Wallet.Bonus : Wallet.Cash,
                    o.BonusToCash ? Wallet.Cash : Wallet.Bonus,
                    Money(o.Amount, o.Currency)),
                _ => null,
            };

            if (command is null)
            {
                return;
            }

            try
            {
                var result = await SendAsync(command);
                if (result.Replayed)
                {
                    // Only resolving an already-resolved withdrawal the same way is a natural replay here.
                    Assert.True(command is CompleteWithdrawal or ReverseWithdrawal, command.GetType().Name);
                    return;
                }

                _successes.Add((command, result));
                switch (command)
                {
                    case PlaceBet bet:
                        _bets.Add(bet);
                        break;
                    case RequestWithdrawal withdrawal:
                        _withdrawals.Add(withdrawal.WithdrawalId);
                        break;
                }
            }
            catch (LedgerException ex) when (ex is InsufficientFundsException or InvalidLedgerStateException)
            {
                // Legitimate rejections in a random history; the invariants must hold regardless.
            }
        }

        public void AssertInvariants()
        {
            // 1. Every journal entry balances, per currency.
            foreach (var entry in _store.Entries)
            {
                Assert.All(entry.Postings.GroupBy(p => p.Currency), g => Assert.Equal(0, g.Sum(p => p.AmountMinor)));
            }

            // 2. The sum of all account balances is zero in every currency.
            var postingsByAccount = _store.Entries.SelectMany(e => e.Postings).ToLookup(p => p.AccountId);
            foreach (var currency in _store.Accounts.GroupBy(a => a.Currency))
            {
                Assert.Equal(0, currency.Sum(a => BalanceOf(a, postingsByAccount)));
            }

            // 3. Stored wallet balances equal their postings, and never go negative.
            foreach (var wallet in _store.Accounts.Where(a => a.TracksBalance))
            {
                Assert.Equal(postingsByAccount[wallet.Id].Sum(p => p.AmountMinor), wallet.BalanceMinor);
                Assert.True(wallet.BalanceMinor >= 0, $"{wallet.Code} is negative");
            }

            // 4. Exactly one entry per successful command; replays added nothing.
            Assert.Equal(_successes.Count, _store.Entries.Count);

            // 5. Money held in suspense is exactly the sum of open withdrawals.
            foreach (var suspense in _store.Accounts.Where(a => a.Type == AccountType.PspSuspense))
            {
                var open = _store.Withdrawals
                    .Where(w => w.Status == WithdrawalStatus.Reserved && w.Currency == suspense.Currency)
                    .Sum(w => w.AmountMinor);
                Assert.Equal(open, BalanceOf(suspense, postingsByAccount));
            }
        }

        private async Task ReplayAsync(int index)
        {
            if (_successes.Count == 0)
            {
                return;
            }

            var (command, original) = _successes[index % _successes.Count];
            var replayed = await SendAsync(command);

            Assert.True(replayed.Replayed);
            Assert.Equal(original.EntryId, replayed.EntryId);
            Assert.Equal(original.Postings, replayed.Postings);
        }

        private Task<PostingResult> SendAsync(ICommand command)
        {
            var clock = TimeProvider.System;
            var ct = CancellationToken.None;
            return command switch
            {
                SettleDeposit c => new SettleDepositHandler(_store, clock).HandleAsync(c, ct),
                PlaceBet c => new PlaceBetHandler(_store, clock).HandleAsync(c, ct),
                CreditWin c => new CreditWinHandler(_store, clock).HandleAsync(c, ct),
                RollbackBet c => new RollbackBetHandler(_store, clock).HandleAsync(c, ct),
                RequestWithdrawal c => new RequestWithdrawalHandler(_store, clock).HandleAsync(c, ct),
                CompleteWithdrawal c => new CompleteWithdrawalHandler(_store, clock).HandleAsync(c, ct),
                ReverseWithdrawal c => new ReverseWithdrawalHandler(_store, clock).HandleAsync(c, ct),
                GrantBonus c => new GrantBonusHandler(_store, clock).HandleAsync(c, ct),
                TransferBetweenWallets c => new TransferBetweenWalletsHandler(_store, clock).HandleAsync(c, ct),
                _ => throw new InvalidOperationException(command.GetType().Name),
            };
        }

        private RollbackBet Rollback(PlaceBet bet) => new(NextId("rb"), bet.PlayerId, bet.TransactionId);

        private string NextId(string prefix) => $"{prefix}-{++_sequence}";

        private static long BalanceOf(Account account, ILookup<Guid, Ledgerline.Domain.Journal.Posting> postings) =>
            account.BalanceMinor ?? postings[account.Id].Sum(p => p.AmountMinor);

        private static Money Money(long amount, int currency) => new(amount, Currencies[currency]);
    }
}
