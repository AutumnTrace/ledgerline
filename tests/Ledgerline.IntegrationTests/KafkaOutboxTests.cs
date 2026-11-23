using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Ledgerline.Domain.Journal;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Persistence;
using Ledgerline.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.IntegrationTests;

/// <summary>
/// The whole pipeline on real infrastructure: HTTP command, PostgreSQL transaction with the outbox row,
/// the dispatcher, Kafka, and the inbox-deduplicated consumer feeding the round projection.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaOutboxTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly string _topic = $"ledger.entries.it-{Guid.NewGuid():N}";
    private PostgresApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new PostgresApiFactory(await containers.CreateDatabaseAsync(), containers.KafkaBootstrapServers, _topic);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Committed_entries_reach_Kafka_in_order_with_their_headers()
    {
        var posted = new List<Guid>
        {
            (await _client.PostOkAsync("/v1/deposits", new { transactionId = "dep-1", provider = "northwind-pay", playerId = "p-k1", currency = "EUR", amountMinor = 10_000 })).GetProperty("entryId").GetGuid(),
        };
        for (var i = 1; i <= 5; i++)
        {
            posted.Add((await _client.PostOkAsync("/v1/wallet/bets", new { transactionId = $"k1-bet-{i}", playerId = "p-k1", roundId = "r-k1", currency = "EUR", amountMinor = 100 * i })).GetProperty("entryId").GetGuid());
        }

        var messages = await ConsumeAsync(m => m.Message.Key == "p-k1", count: posted.Count);

        Assert.Equal(posted, messages.Select(m => Payload(m).EntryId));
        Assert.Single(messages.Select(m => m.Partition.Value).Distinct());
        var first = messages[0];
        Assert.Equal("ledger.entry.posted.v1", Header(first, LedgerHeaders.MessageType));
        Assert.Equal("application/json", Header(first, LedgerHeaders.ContentType));
        Assert.Equal(Payload(first).MessageId.ToString(), Header(first, LedgerHeaders.MessageId));
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$", Header(first, LedgerHeaders.TraceParent));

        var bet = Payload(messages[^1]);
        Assert.Equal(EntryType.BetDebit, bet.EntryType);
        Assert.Equal(10_000 - 1_500, bet.Postings[0].BalanceAfterMinor);

        // Every outbox row was marked processed once the broker acknowledged it.
        await EventuallyAsync(async () => await QueryAsync(db => db.OutboxMessages.AllAsync(m => m.ProcessedAt != null)));
    }

    [Fact]
    public async Task The_projection_is_fed_through_Kafka_and_ignores_redelivered_messages()
    {
        await _client.PostOkAsync("/v1/deposits", new { transactionId = "dep-2", provider = "northwind-pay", playerId = "p-k2", currency = "EUR", amountMinor = 10_000 });
        var bet = await _client.PostOkAsync("/v1/wallet/bets", new { transactionId = "k2-bet", playerId = "p-k2", roundId = "r-k2", currency = "EUR", amountMinor = 2_500 });
        await _client.EventuallyAsync("/v1/rounds/r-k2", r => r.GetProperty("entries").GetInt32() == 1);

        // Simulate at-least-once delivery: the same message arrives twice more.
        var betEntryId = bet.GetProperty("entryId").GetGuid();
        var original = await ConsumeAsync(m => Payload(m).EntryId == betEntryId, count: 1);
        await RedeliverAsync(original[0].Message, times: 2);

        // A later message on the same key (same partition) proves the duplicates were consumed first.
        await _client.PostOkAsync("/v1/wallet/wins", new { transactionId = "k2-win", playerId = "p-k2", roundId = "r-k2", currency = "EUR", amountMinor = 1_000 });
        var round = await _client.EventuallyAsync("/v1/rounds/r-k2", r => r.GetProperty("entries").GetInt32() >= 2);

        Assert.Equal(2, round.GetProperty("entries").GetInt32());
        Assert.Equal(2_500, round.GetProperty("bets").GetProperty("minor").GetInt64());
        Assert.Equal(1_500, round.GetProperty("net").GetProperty("minor").GetInt64());
        var inboxRows = await QueryAsync(db => db.InboxMessages.CountAsync(m => m.MessageId == Payload(original[0]).MessageId));
        Assert.Equal(1, inboxRows);
    }

    private async Task RedeliverAsync(Message<string, string> message, int times)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = containers.KafkaBootstrapServers }).Build();
        for (var i = 0; i < times; i++)
        {
            await producer.ProduceAsync(_topic, new Message<string, string> { Key = message.Key, Value = message.Value, Headers = message.Headers }, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Reads the topic from the beginning with a throwaway consumer group until enough messages match.</summary>
    private Task<List<ConsumeResult<string, string>>> ConsumeAsync(Func<ConsumeResult<string, string>, bool> match, int count) =>
        Task.Run(() =>
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = containers.KafkaBootstrapServers,
                GroupId = $"it-reader-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(_topic);

            var found = new List<ConsumeResult<string, string>>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (found.Count < count && DateTimeOffset.UtcNow < deadline)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is not null && match(result))
                {
                    found.Add(result);
                }
            }

            consumer.Close();
            Assert.True(found.Count == count, $"Expected {count} messages on {_topic}, got {found.Count}.");
            return found;
        });

    private async Task<T> QueryAsync<T>(Func<LedgerDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<LedgerDbContext>());
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Condition not met in time.");
    }

    private static Ledgerline.Application.IntegrationEvents.LedgerEntryPostedV1 Payload(ConsumeResult<string, string> result) =>
        JsonSerializer.Deserialize(result.Message.Value, LedgerJsonContext.Default.LedgerEntryPostedV1)!;

    private static string? Header(ConsumeResult<string, string> result, string name) =>
        result.Message.Headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
