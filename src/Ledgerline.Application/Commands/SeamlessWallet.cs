using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.Commands;

/// <summary>
/// Seamless-wallet calls from a game server. Bets, wins and rollbacks share one transaction-id space,
/// so a win reusing a bet's transaction id is rejected rather than silently treated as a replay.
/// </summary>
internal static class WalletKeys
{
    public static string For(string transactionId) => $"wallet:{transactionId}";
}

public sealed record PlaceBet(string TransactionId, string PlayerId, Money Amount, string RoundId) : ICommand
{
    public string IdempotencyKey => WalletKeys.For(TransactionId);

    public string Fingerprint => $"{PlayerId}|{Amount.Currency.Code}|{Amount.Minor}|{RoundId}";
}

public sealed record CreditWin(string TransactionId, string PlayerId, Money Amount, string RoundId) : ICommand
{
    public string IdempotencyKey => WalletKeys.For(TransactionId);

    public string Fingerprint => $"{PlayerId}|{Amount.Currency.Code}|{Amount.Minor}|{RoundId}";
}

/// <summary>Cancels a bet by posting its exact mirror image. A bet can be rolled back at most once.</summary>
public sealed record RollbackBet(string TransactionId, string PlayerId, string OriginalTransactionId) : ICommand
{
    public string IdempotencyKey => WalletKeys.For(TransactionId);

    public string Fingerprint => $"{PlayerId}|{OriginalTransactionId}";
}

internal sealed class PlaceBetHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<PlaceBet>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(PlaceBet command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        var cash = await AccountAsync(AccountKey.PlayerCash(command.PlayerId, amount.Currency), cancellationToken);
        var house = await AccountAsync(AccountKey.HouseGaming(amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.BetDebit, command.TransactionId, command.PlayerId, [new Leg(cash, -amount), new Leg(house, amount)])
        {
            RoundId = command.RoundId,
        };
    }
}

internal sealed class CreditWinHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<CreditWin>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(CreditWin command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        var house = await AccountAsync(AccountKey.HouseGaming(amount.Currency), cancellationToken);
        var cash = await AccountAsync(AccountKey.PlayerCash(command.PlayerId, amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.WinCredit, command.TransactionId, command.PlayerId, [new Leg(house, -amount), new Leg(cash, amount)])
        {
            RoundId = command.RoundId,
        };
    }
}

internal sealed class RollbackBetHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<RollbackBet>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(RollbackBet command, CancellationToken cancellationToken)
    {
        var bet = await Store.FindEntryAsync(WalletKeys.For(command.OriginalTransactionId), cancellationToken);

        // Another player's transaction is reported as missing, so ids cannot be probed across players.
        if (bet is null || !string.Equals(bet.PlayerId, command.PlayerId, StringComparison.Ordinal))
        {
            throw new LedgerNotFoundException("Bet", command.OriginalTransactionId);
        }

        if (bet.Type != EntryType.BetDebit)
        {
            throw new InvalidLedgerStateException($"Transaction '{command.OriginalTransactionId}' is a {bet.Type}; only bets can be rolled back.");
        }

        if (await Store.IsReversedAsync(bet.Id, cancellationToken))
        {
            throw new InvalidLedgerStateException($"Bet '{command.OriginalTransactionId}' has already been rolled back.");
        }

        var accounts = await Store.GetAccountsAsync(bet.Postings.Select(p => p.AccountId), cancellationToken);
        var legs = bet.Postings.Select(p => new Leg(accounts[p.AccountId], p.Amount.Negate())).ToList();

        return new PostingPlan(EntryType.BetRollback, command.TransactionId, command.PlayerId, legs)
        {
            RoundId = bet.RoundId,
            ReversesEntryId = bet.Id,
        };
    }
}
