using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Application.Telemetry;
using Ledgerline.Infrastructure.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledgerline.Infrastructure.Messaging;

/// <summary>Deserialises a ledger message and hands it to the projector in a fresh scope.</summary>
public sealed partial class LedgerMessageHandler(IServiceScopeFactory scopes, ILogger<LedgerMessageHandler> logger)
{
    public async Task HandleAsync(Guid messageId, string payload, string? traceParent, CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(traceParent, null, isRemote: true, out var parent);
        using var activity = LedgerTelemetry.ActivitySource.StartActivity("ledger.entries.v1 process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.message.id", messageId.ToString());

        var message = JsonSerializer.Deserialize(payload, LedgerJsonContext.Default.LedgerEntryPostedV1)
            ?? throw new InvalidOperationException($"Message {messageId} has an empty payload.");

        await using var scope = scopes.CreateAsyncScope();
        var projector = scope.ServiceProvider.GetRequiredService<RoundSummaryProjector>();
        if (!await projector.ProjectAsync(messageId, message, cancellationToken))
        {
            activity?.SetTag("ledger.duplicate", true);
            LogDuplicate(logger, messageId);
        }
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Skipped duplicate delivery of {MessageId}")]
    private static partial void LogDuplicate(ILogger logger, Guid messageId);
}

/// <summary>Consumes the in-memory bus (local mode).</summary>
public sealed partial class InMemoryLedgerConsumer(InMemoryMessageBus bus, LedgerMessageHandler handler, ILogger<InMemoryLedgerConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in bus.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await handler.HandleAsync(message.MessageId, message.Payload, message.TraceParent, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex, message.MessageId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(EventId = 3202, Level = LogLevel.Error, Message = "Projecting {MessageId} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid messageId);
}

/// <summary>
/// Kafka consumer with manual offset storage: the offset is stored only after the projection committed,
/// so a crash replays the message (at-least-once) and the inbox makes the replay harmless.
/// </summary>
public sealed partial class KafkaLedgerConsumer(
    IOptions<MessagingOptions> options,
    LedgerMessageHandler handler,
    TimeProvider clock,
    ILogger<KafkaLedgerConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var kafka = options.Value.Kafka;
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = kafka.ConsumerGroup,
            ClientId = kafka.ClientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        }).Build();
        consumer.Subscribe(options.Value.Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    // Consume blocks for at most the timeout, so shutdown stays responsive.
                    result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    // E.g. the topic is not there yet. Transient: keep polling instead of stopping the host.
                    LogConsumeError(logger, ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
                    continue;
                }

                if (result is null)
                {
                    continue;
                }

                try
                {
                    var messageId = Guid.Parse(Header(result.Message, LedgerHeaders.MessageId) ?? throw new InvalidOperationException("Missing message-id header."));
                    await handler.HandleAsync(messageId, result.Message.Value, Header(result.Message, LedgerHeaders.TraceParent), stoppingToken);
                    consumer.StoreOffset(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Rewind and try the same message again after a pause, rather than skipping it.
                    LogFailed(logger, ex, result.TopicPartitionOffset.ToString());
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            consumer.Close();
        }
    }

    private static string? Header(Message<string, string> message, string name) =>
        message.Headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    [LoggerMessage(EventId = 3205, Level = LogLevel.Warning, Message = "Kafka consume error: {Reason}")]
    private static partial void LogConsumeError(ILogger logger, string reason);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Error, Message = "Processing {Offset} failed; retrying the same message")]
    private static partial void LogFailed(ILogger logger, Exception exception, string offset);
}

/// <summary>Creates the ledger topic on startup when asked to (local and CI brokers). Runs before the dispatcher and consumer start.</summary>
public sealed partial class KafkaTopicInitializer(IOptions<MessagingOptions> options, ILogger<KafkaTopicInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var kafka = options.Value.Kafka;
        if (!kafka.CreateTopic)
        {
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = options.Value.Topic,
                NumPartitions = kafka.TopicPartitions,
                ReplicationFactor = kafka.ReplicationFactor,
            }]);
            LogCreated(logger, options.Value.Topic, kafka.TopicPartitions);
        }
        catch (CreateTopicsException ex) when (ex.Results.TrueForAll(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // Already there.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 3204, Level = LogLevel.Information, Message = "Created topic {Topic} with {Partitions} partitions")]
    private static partial void LogCreated(ILogger logger, string topic, int partitions);
}
