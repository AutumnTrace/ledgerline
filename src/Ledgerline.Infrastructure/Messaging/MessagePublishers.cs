using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledgerline.Infrastructure.Messaging;

/// <summary>A message on its way out, as read from the outbox.</summary>
public sealed record OutboundMessage(
    Guid MessageId,
    string Topic,
    string Key,
    string Type,
    string Payload,
    string? TraceParent,
    DateTimeOffset OccurredAt);

/// <param name="Delivered">How many messages, from the start of the batch, the broker acknowledged.</param>
/// <param name="Error">Why the first undelivered message failed, if any did.</param>
public sealed record PublishOutcome(int Delivered, Exception? Error);

/// <summary>Transport behind the outbox dispatcher.</summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes the batch in order and reports the acknowledged prefix. Anything after the first failure
    /// is treated as unpublished and retried, so consumers may see duplicates but never a gap.
    /// </summary>
    Task<PublishOutcome> PublishAsync(IReadOnlyList<OutboundMessage> batch, CancellationToken cancellationToken);
}

/// <summary>Message headers on the wire.</summary>
public static class LedgerHeaders
{
    public const string MessageId = "message-id";
    public const string MessageType = "message-type";
    public const string ContentType = "content-type";
    public const string TraceParent = "traceparent";
}

/// <summary>
/// Local transport: hands messages to in-process consumers through a channel and logs each one.
/// It keeps <c>dotnet run</c> working with no broker while exercising the same outbox, dispatcher and inbox code.
/// </summary>
public sealed partial class InMemoryMessageBus(IOptions<MessagingOptions> options, ILogger<InMemoryMessageBus> logger) : IMessagePublisher
{
    private readonly Channel<OutboundMessage> _channel = Channel.CreateUnbounded<OutboundMessage>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<OutboundMessage> Reader => _channel.Reader;

    public async Task<PublishOutcome> PublishAsync(IReadOnlyList<OutboundMessage> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var message in batch)
        {
            if (options.Value.EnableRoundProjection)
            {
                await _channel.Writer.WriteAsync(message, cancellationToken);
            }

            LogPublished(logger, message.Type, message.MessageId, message.Topic, message.Key);
        }

        return new PublishOutcome(batch.Count, null);
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Published {MessageType} {MessageId} to {Topic} (in-memory) with key {Key}")]
    private static partial void LogPublished(ILogger logger, string messageType, Guid messageId, string topic, string key);
}

/// <summary>
/// Kafka transport. The producer is idempotent with <c>acks=all</c>, so a broker-side retry never
/// duplicates or reorders messages within a partition; the key (player id) picks the partition.
/// </summary>
public sealed class KafkaMessagePublisher : IMessagePublisher, IDisposable
{
    private static readonly byte[] JsonContentType = Encoding.UTF8.GetBytes("application/json");

    private readonly Lazy<IProducer<string, string>> _producer;

    public KafkaMessagePublisher(IOptions<MessagingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var kafka = options.Value.Kafka;
        _producer = new Lazy<IProducer<string, string>>(() => new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            ClientId = kafka.ClientId,
            EnableIdempotence = true,
            Acks = Acks.All,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = 30_000,
        }).Build());
    }

    public async Task<PublishOutcome> PublishAsync(IReadOnlyList<OutboundMessage> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Hand the whole batch to librdkafka first so it can pipeline, then wait for each acknowledgement.
        var deliveries = batch.Select(m => _producer.Value.ProduceAsync(m.Topic, ToKafka(m), cancellationToken)).ToList();
        for (var i = 0; i < deliveries.Count; i++)
        {
            try
            {
                await deliveries[i];
            }
            catch (ProduceException<string, string> ex)
            {
                await SettleRemainingAsync(deliveries, i + 1);
                return new PublishOutcome(i, ex);
            }
        }

        return new PublishOutcome(batch.Count, null);
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }

    private static Message<string, string> ToKafka(OutboundMessage message)
    {
        var headers = new Headers
        {
            { LedgerHeaders.MessageId, Encoding.UTF8.GetBytes(message.MessageId.ToString()) },
            { LedgerHeaders.MessageType, Encoding.UTF8.GetBytes(message.Type) },
            { LedgerHeaders.ContentType, JsonContentType },
        };
        if (message.TraceParent is not null)
        {
            headers.Add(LedgerHeaders.TraceParent, Encoding.UTF8.GetBytes(message.TraceParent));
        }

        return new Message<string, string> { Key = message.Key, Value = message.Payload, Headers = headers };
    }

    /// <summary>Observe the remaining deliveries so none fault unobserved; their outcome does not matter, they will be re-sent.</summary>
    private static async Task SettleRemainingAsync(List<Task<DeliveryResult<string, string>>> deliveries, int from)
    {
        try
        {
            await Task.WhenAll(deliveries.Skip(from));
        }
        catch (ProduceException<string, string>)
        {
            // Already reported through the first failure.
        }
    }
}
