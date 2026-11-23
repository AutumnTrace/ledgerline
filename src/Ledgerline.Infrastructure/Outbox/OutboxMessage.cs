using System.Diagnostics;
using System.Text.Json;
using Ledgerline.Application.IntegrationEvents;
using Ledgerline.Domain;
using Ledgerline.Domain.Journal;
using Ledgerline.Infrastructure.Messaging;

namespace Ledgerline.Infrastructure.Outbox;

/// <summary>
/// A message waiting to be published, written in the same transaction as the change it describes.
/// If the transaction rolls back, the message never existed; if it commits, the dispatcher will
/// publish it eventually, however long the broker is down.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    /// <summary>Insertion order. The dispatcher publishes in this order.</summary>
    public long Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string Topic { get; private set; } = null!;

    public string MessageKey { get; private set; } = null!;

    public string MessageType { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    /// <summary>W3C trace context of the request that wrote the message, so the trace continues through Kafka.</summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage From(JournalEntryPosted posted, string topic)
    {
        ArgumentNullException.ThrowIfNull(posted);
        var contract = new LedgerEntryPostedV1(
            posted.EventId,
            posted.EntryId,
            posted.EntryType,
            posted.TransactionId,
            posted.PlayerId,
            posted.RoundId,
            posted.ReversesEntryId,
            posted.OccurredAt,
            [.. posted.Legs.Select(l => new LedgerPostingV1(l.AccountId, l.AccountCode, l.AccountType, l.Currency, l.AmountMinor, l.BalanceAfterMinor))]);

        return new OutboxMessage
        {
            MessageId = posted.EventId,
            Topic = topic,
            MessageKey = posted.PlayerId,
            MessageType = LedgerEntryPostedV1.MessageType,
            Payload = JsonSerializer.Serialize(contract, LedgerJsonContext.Default.LedgerEntryPostedV1),
            TraceParent = Activity.Current?.Id,
            OccurredAt = LedgerTime.Normalize(posted.OccurredAt),
        };
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        Attempts++;
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        Attempts++;
        LastError = error.Length <= 2000 ? error : error[..2000];
    }
}
