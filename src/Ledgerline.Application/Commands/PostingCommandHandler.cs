using System.Security.Cryptography;
using System.Text;
using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.Commands;

/// <summary>The entry a command wants to post, before idempotency metadata is attached.</summary>
public sealed record PostingPlan(EntryType Type, string TransactionId, string PlayerId, IReadOnlyList<Leg> Legs)
{
    public string? RoundId { get; init; }

    public Guid? ReversesEntryId { get; init; }

    /// <summary>Runs after the entry is posted and before the unit of work commits (e.g. a withdrawal state change).</summary>
    public Action<JournalEntry, DateTimeOffset>? OnPosted { get; init; }
}

/// <summary>
/// Template for every money-moving command: replay check, plan, post, commit. Subclasses only decide
/// which accounts move and by how much; idempotency is handled once, here.
/// </summary>
internal abstract class PostingCommandHandler<TCommand>(ILedgerStore store, TimeProvider clock) : ICommandHandler<TCommand>
    where TCommand : class, ICommand
{
    protected ILedgerStore Store { get; } = store;

    public async Task<PostingResult> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestHash = RequestHash.Of(command);

        var existing = await Store.FindEntryAsync(command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException(command.IdempotencyKey);
            }

            var accounts = await Store.GetAccountsAsync(existing.Postings.Select(p => p.AccountId), cancellationToken);
            return PostingResult.From(existing, accounts, replayed: true);
        }

        var plan = await PlanAsync(command, cancellationToken);
        var now = clock.GetUtcNow();
        var header = new EntryHeader(plan.Type, command.IdempotencyKey, requestHash, plan.TransactionId, plan.PlayerId)
        {
            RoundId = plan.RoundId,
            ReversesEntryId = plan.ReversesEntryId,
        };

        var entry = JournalEntry.Post(header, plan.Legs, now);
        plan.OnPosted?.Invoke(entry, now);
        Store.Add(entry);
        await Store.SaveChangesAsync(cancellationToken);

        return PostingResult.From(entry, plan.Legs.ToDictionary(l => l.Account.Id, l => l.Account), replayed: false);
    }

    protected abstract Task<PostingPlan> PlanAsync(TCommand command, CancellationToken cancellationToken);

    protected static Money Positive(Money amount, string field = "amountMinor") =>
        amount.IsPositive ? amount : throw new LedgerValidationException(field, "Amount must be greater than zero.");

    protected Task<Account> AccountAsync(AccountKey key, CancellationToken cancellationToken) =>
        Store.GetOrOpenAccountAsync(key, cancellationToken);
}

internal static class RequestHash
{
    /// <summary>SHA-256 of the command type and its fingerprint, so a bet and a win can never share a key.</summary>
    public static string Of(ICommand command)
    {
        var canonical = $"{command.GetType().Name}|{command.Fingerprint}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
