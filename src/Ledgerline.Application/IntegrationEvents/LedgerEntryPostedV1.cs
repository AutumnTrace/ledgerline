using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.IntegrationEvents;

/// <summary>
/// Wire contract published to <c>ledger.entries.v1</c>, one message per journal entry, keyed by player id
/// so a player's entries stay ordered within a partition. Versioned separately from the domain event:
/// the domain can change shape without breaking consumers.
/// </summary>
/// <param name="MessageId">Unique per message. Consumers deduplicate on it (delivery is at-least-once).</param>
public sealed record LedgerEntryPostedV1(
    Guid MessageId,
    Guid EntryId,
    EntryType EntryType,
    string TransactionId,
    string PlayerId,
    string? RoundId,
    Guid? ReversesEntryId,
    DateTimeOffset PostedAt,
    IReadOnlyList<LedgerPostingV1> Postings)
{
    public const string MessageType = "ledger.entry.posted.v1";
}

public sealed record LedgerPostingV1(
    Guid AccountId,
    string AccountCode,
    AccountType AccountType,
    string Currency,
    long AmountMinor,
    long? BalanceAfterMinor);
