using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.Commands;

/// <summary>A payment provider confirmed a player's deposit. Idempotent per provider transaction id.</summary>
public sealed record SettleDeposit(string TransactionId, string Provider, string PlayerId, Money Amount) : ICommand
{
    public string IdempotencyKey => $"deposit:{Provider}:{TransactionId}";

    public string Fingerprint => $"{PlayerId}|{Amount.Currency.Code}|{Amount.Minor}";
}

internal sealed class SettleDepositHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<SettleDeposit>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(SettleDeposit command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        var clearing = await AccountAsync(AccountKey.PspClearing(command.Provider, amount.Currency), cancellationToken);
        var cash = await AccountAsync(AccountKey.PlayerCash(command.PlayerId, amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.DepositSettled, command.TransactionId, command.PlayerId, [new Leg(clearing, -amount), new Leg(cash, amount)]);
    }
}
