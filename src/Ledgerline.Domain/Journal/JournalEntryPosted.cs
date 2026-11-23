using Ledgerline.Domain.Accounts;

namespace Ledgerline.Domain.Journal;

/// <summary>Raised once per journal entry. The outbox turns it into a <c>ledger.entries.v1</c> message.</summary>
public sealed record JournalEntryPosted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid EntryId,
    EntryType EntryType,
    string TransactionId,
    string PlayerId,
    string? RoundId,
    Guid? ReversesEntryId,
    IReadOnlyList<PostedLeg> Legs) : IDomainEvent;

public sealed record PostedLeg(
    Guid AccountId,
    string AccountCode,
    AccountType AccountType,
    string Currency,
    long AmountMinor,
    long? BalanceAfterMinor);
