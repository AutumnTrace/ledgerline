using Ledgerline.Domain.Journal;

namespace Ledgerline.Infrastructure.Projections;

/// <summary>
/// Read model built asynchronously from <c>ledger.entries.v1</c>: totals per game round.
/// Net (gross gaming revenue for the round) = bets - rolled back - wins.
/// </summary>
public sealed class RoundSummary
{
    private RoundSummary()
    {
    }

    public string RoundId { get; private set; } = null!;

    public string PlayerId { get; private set; } = null!;

    public string Currency { get; private set; } = null!;

    public long BetMinor { get; private set; }

    public long WinMinor { get; private set; }

    public long RolledBackMinor { get; private set; }

    public int Entries { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RoundSummary Start(string roundId, string playerId, string currency) =>
        new() { RoundId = roundId, PlayerId = playerId, Currency = currency };

    public void Apply(EntryType type, long amountMinor, DateTimeOffset at)
    {
        switch (type)
        {
            case EntryType.BetDebit:
                BetMinor += amountMinor;
                break;
            case EntryType.WinCredit:
                WinMinor += amountMinor;
                break;
            case EntryType.BetRollback:
                RolledBackMinor += amountMinor;
                break;
            default:
                return;
        }

        Entries++;
        UpdatedAt = at;
    }
}

/// <summary>Processed-message log for idempotent consumers. The primary key is the deduplication.</summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    public InboxMessage(string consumer, Guid messageId, DateTimeOffset processedAt)
    {
        Consumer = consumer;
        MessageId = messageId;
        ProcessedAt = processedAt;
    }

    public string Consumer { get; private set; } = null!;

    public Guid MessageId { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}
