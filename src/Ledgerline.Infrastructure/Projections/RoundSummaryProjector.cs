using Ledgerline.Application.IntegrationEvents;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Infrastructure.Projections;

/// <summary>
/// An idempotent consumer of <c>ledger.entries.v1</c>. The inbox row and the projection update commit
/// together, so a redelivered message (at-least-once) is recognised and skipped instead of double-counted.
/// </summary>
public sealed class RoundSummaryProjector(LedgerDbContext db, TimeProvider clock)
{
    public const string ConsumerName = "round-summaries";

    /// <summary>Returns false when the message was already processed.</summary>
    public async Task<bool> ProjectAsync(Guid messageId, LedgerEntryPostedV1 message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await db.InboxMessages.AnyAsync(m => m.Consumer == ConsumerName && m.MessageId == messageId, cancellationToken))
        {
            return false;
        }

        var now = clock.GetUtcNow();
        db.InboxMessages.Add(new InboxMessage(ConsumerName, messageId, now));

        var wallet = message.Postings.FirstOrDefault(p => p.AccountType == AccountType.PlayerCash);
        if (message.RoundId is not null && wallet is not null && message.EntryType is EntryType.BetDebit or EntryType.WinCredit or EntryType.BetRollback)
        {
            var summary = await db.RoundSummaries.FindAsync([message.RoundId], cancellationToken);
            if (summary is null)
            {
                summary = RoundSummary.Start(message.RoundId, message.PlayerId, wallet.Currency);
                db.RoundSummaries.Add(summary);
            }

            summary.Apply(message.EntryType, Math.Abs(wallet.AmountMinor), now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsWriteConflict(ex))
        {
            // Two deliveries of the same message raced; the inbox primary key let exactly one win.
            return false;
        }
    }
}
