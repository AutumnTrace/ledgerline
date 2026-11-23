using System.ComponentModel.DataAnnotations;

namespace Ledgerline.Infrastructure.Messaging;

public enum MessageTransport
{
    /// <summary>In-process channel plus a log line per message. No broker needed: <c>dotnet run</c> just works.</summary>
    InMemory,

    /// <summary>Apache Kafka (or any Kafka-compatible broker) through Confluent.Kafka.</summary>
    Kafka,
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public MessageTransport Transport { get; set; } = MessageTransport.InMemory;

    [Required, RegularExpression("^[a-zA-Z0-9._-]{1,249}$")]
    public string Topic { get; set; } = "ledger.entries.v1";

    [Required]
    public KafkaOptions Kafka { get; set; } = new();

    /// <summary>Hosts the round-summary projector (an inbox-deduplicated consumer of the ledger topic) in this process.</summary>
    public bool EnableRoundProjection { get; set; } = true;
}

public sealed class KafkaOptions
{
    /// <summary>Required when <see cref="MessagingOptions.Transport"/> is Kafka, e.g. <c>localhost:9092</c>.</summary>
    public string? BootstrapServers { get; set; }

    [Required]
    public string ClientId { get; set; } = "ledgerline";

    [Required]
    public string ConsumerGroup { get; set; } = "ledgerline-round-projector";

    /// <summary>Create the topic at startup if it does not exist (local and CI brokers).</summary>
    public bool CreateTopic { get; set; } = true;

    [Range(1, 1024)]
    public int TopicPartitions { get; set; } = 3;

    [Range(1, 16)]
    public short ReplicationFactor { get; set; } = 1;
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Run the dispatcher in this process. Turn off to run a dedicated relay instead.</summary>
    public bool Enabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>Readiness reports Degraded when the oldest unpublished message is older than this.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan LagWarningThreshold { get; set; } = TimeSpan.FromSeconds(30);
}
