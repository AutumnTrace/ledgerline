using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Domain.Withdrawals;

namespace Ledgerline.Application.Commands;

/// <summary>
/// Reserves a payout: the money leaves the player's cash wallet immediately and waits in PSP suspense,
/// so it can neither be bet nor withdrawn twice while the provider processes it.
/// </summary>
public sealed record RequestWithdrawal(string WithdrawalId, string Provider, string PlayerId, Money Amount) : ICommand
{
    public string IdempotencyKey => $"withdrawal:{WithdrawalId}:reserve";

    public string Fingerprint => $"{Provider}|{PlayerId}|{Amount.Currency.Code}|{Amount.Minor}";
}

/// <summary>The provider paid the player out: suspense moves to PSP clearing.</summary>
public sealed record CompleteWithdrawal(string WithdrawalId) : ICommand
{
    public string IdempotencyKey => $"withdrawal:{WithdrawalId}:complete";

    public string Fingerprint => string.Empty;
}

/// <summary>The payout failed or was cancelled: suspense goes back to the player's cash wallet.</summary>
public sealed record ReverseWithdrawal(string WithdrawalId, string Reason) : ICommand
{
    public string IdempotencyKey => $"withdrawal:{WithdrawalId}:reverse";

    // The free-text reason is informational, so a retry with a reworded reason is still a replay.
    public string Fingerprint => string.Empty;
}

internal sealed class RequestWithdrawalHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<RequestWithdrawal>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(RequestWithdrawal command, CancellationToken cancellationToken)
    {
        var amount = Positive(command.Amount);
        var cash = await AccountAsync(AccountKey.PlayerCash(command.PlayerId, amount.Currency), cancellationToken);
        var suspense = await AccountAsync(AccountKey.PspSuspense(command.Provider, amount.Currency), cancellationToken);

        return new PostingPlan(EntryType.WithdrawalReserved, command.WithdrawalId, command.PlayerId, [new Leg(cash, -amount), new Leg(suspense, amount)])
        {
            OnPosted = (entry, now) =>
                Store.Add(Withdrawal.Reserve(command.WithdrawalId, command.PlayerId, command.Provider, amount, entry.Id, now)),
        };
    }
}

internal sealed class CompleteWithdrawalHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<CompleteWithdrawal>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(CompleteWithdrawal command, CancellationToken cancellationToken)
    {
        var withdrawal = await Store.FindWithdrawalAsync(command.WithdrawalId, cancellationToken)
            ?? throw new LedgerNotFoundException("Withdrawal", command.WithdrawalId);
        withdrawal.EnsurePending();

        var suspense = await AccountAsync(AccountKey.PspSuspense(withdrawal.Provider, withdrawal.Currency), cancellationToken);
        var clearing = await AccountAsync(AccountKey.PspClearing(withdrawal.Provider, withdrawal.Currency), cancellationToken);

        return new PostingPlan(
            EntryType.WithdrawalCompleted,
            withdrawal.WithdrawalId,
            withdrawal.PlayerId,
            [new Leg(suspense, -withdrawal.Amount), new Leg(clearing, withdrawal.Amount)])
        {
            OnPosted = (entry, now) => withdrawal.Complete(entry.Id, now),
        };
    }
}

internal sealed class ReverseWithdrawalHandler(ILedgerStore store, TimeProvider clock) : PostingCommandHandler<ReverseWithdrawal>(store, clock)
{
    protected override async Task<PostingPlan> PlanAsync(ReverseWithdrawal command, CancellationToken cancellationToken)
    {
        var reason = Withdrawal.RequireReason(command.Reason);
        var withdrawal = await Store.FindWithdrawalAsync(command.WithdrawalId, cancellationToken)
            ?? throw new LedgerNotFoundException("Withdrawal", command.WithdrawalId);
        withdrawal.EnsurePending();

        var suspense = await AccountAsync(AccountKey.PspSuspense(withdrawal.Provider, withdrawal.Currency), cancellationToken);
        var cash = await AccountAsync(AccountKey.PlayerCash(withdrawal.PlayerId, withdrawal.Currency), cancellationToken);

        return new PostingPlan(
            EntryType.WithdrawalReversed,
            withdrawal.WithdrawalId,
            withdrawal.PlayerId,
            [new Leg(suspense, -withdrawal.Amount), new Leg(cash, withdrawal.Amount)])
        {
            OnPosted = (entry, now) => withdrawal.Reverse(entry.Id, reason, now),
        };
    }
}
