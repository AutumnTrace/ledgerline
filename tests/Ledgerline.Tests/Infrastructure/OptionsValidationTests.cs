using Ledgerline.Infrastructure;
using Ledgerline.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ledgerline.Tests.Infrastructure;

/// <summary>A bad setting stops the app at startup, including settings nested under <c>Messaging:Kafka</c>.</summary>
public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData("Messaging:Kafka:TopicPartitions", "0")]
    [InlineData("Messaging:Kafka:TopicPartitions", "2000")]
    [InlineData("Messaging:Kafka:ReplicationFactor", "0")]
    [InlineData("Messaging:Kafka:ClientId", "")]
    [InlineData("Messaging:Kafka:ConsumerGroup", "")]
    public void Invalid_kafka_settings_are_rejected(string key, string value)
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve<MessagingOptions>((key, value)));

        var setting = key.Split(':')[^1];
        Assert.Contains(error.Failures, f => f.Contains(setting, StringComparison.Ordinal));
    }

    [Fact]
    public void Top_level_settings_are_rejected_too()
    {
        Assert.Throws<OptionsValidationException>(() => Resolve<OutboxOptions>(("Outbox:BatchSize", "0")));
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        var messaging = Resolve<MessagingOptions>();

        Assert.Equal(3, messaging.Kafka.TopicPartitions);
        Assert.Equal("ledgerline", messaging.Kafka.ClientId);
    }

    private static T Resolve<T>(params (string Key, string Value)[] settings)
        where T : class
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddLedgerInfrastructure(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<T>>().Value;
    }
}
