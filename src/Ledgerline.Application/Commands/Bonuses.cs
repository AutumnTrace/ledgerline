using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.Commands;

/// <summary>Credits promotional money from the operator's bonus pool to a player's bonus wallet.</summary>
public sealed record GrantBonus(string TransactionId, string PlayerId, Money Amount) : ICommand
{
    public string IdempotencyKey => $"bonus:{TransactionId}";

    public string Fingerprint => $"{PlayerId}|{Amount.Currency.Code}|{Amount.Minor}";
}

/// <summary>
/// Moves money between two wallets of the same player, e.g. releasing bonus money into cash once
/// wagering requirements are met (the requirements themselves live outside the ledger).
/// </summary>
public sealed record TransferBetweenWallets(string TransactionId, string PlayerId, Wallet From, Wallet To, Money Amount) : ICommand
{
    public string IdempotencyKey => $"transfer:{TransactionId}";

    public string Fingerprint => $"{PlayerId}|{From}|{To}|{Amount.Currency.Code}|{Amount.Minor}";
}

internal sealed class GrantBonusHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<GrantBonus>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(GrantBonus command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        var pool = await AccountAsync(AccountKey.BonusPool(amount.Currency), cancellationToken);
        var bonus = await AccountAsync(AccountKey.PlayerBonus(command.PlayerId, amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.BonusGranted, command.TransactionId, command.PlayerId, [new Leg(pool, -amount), new Leg(bonus, amount)]);
    }
}

internal sealed class TransferBetweenWalletsHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<TransferBetweenWallets>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(TransferBetweenWallets command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        if (command.From == command.To)
        {
            throw new LedgerValidationException("to", "Source and destination wallets must differ.");
        }

        var from = await AccountAsync(AccountKey.PlayerWallet(command.PlayerId, command.From, amount.Currency), cancellationToken);
        var to = await AccountAsync(AccountKey.PlayerWallet(command.PlayerId, command.To, amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.WalletTransfer, command.TransactionId, command.PlayerId, [new Leg(from, -amount), new Leg(to, amount)]);
    }
}
