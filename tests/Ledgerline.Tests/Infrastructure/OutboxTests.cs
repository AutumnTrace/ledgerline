using System.Text.Json;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Application.IntegrationEvents;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Outbox;
using Ledgerline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Ledgerline.Tests.Infrastructure;

/// <summary>The transactional outbox and its dispatcher, with a recording transport and a fake clock.</summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private readonly RecordingPublisher _publisher = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private LedgerServices _ledger = null!;

    public async ValueTask InitializeAsync() =>
        _ledger = await LedgerServices.CreateAsync(configureServices: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IMessagePublisher>(_publisher));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
        });

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task Every_entry_writes_exactly_one_message_in_the_same_transaction()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        var bet = await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));

        var messages = await OutboxAsync();

        Assert.Equal(2, messages.Count);
        var last = messages[^1];
        Assert.Equal("ledger.entries.v1", last.Topic);
        Assert.Equal("p-1", last.MessageKey);
        Assert.Equal(LedgerEntryPostedV1.MessageType, last.MessageType);
        Assert.Null(last.ProcessedAt);

        var payload = JsonSerializer.Deserialize(last.Payload, LedgerJsonContext.Default.LedgerEntryPostedV1)!;
        Assert.Equal(last.MessageId, payload.MessageId);
        Assert.Equal(bet.EntryId, payload.EntryId);
        Assert.Equal(EntryType.BetDebit, payload.EntryType);
        Assert.Equal("round-1", payload.RoundId);
        Assert.Equal(
            new[] { ("player:p-1:cash:EUR", AccountType.PlayerCash, -2_500L, (long?)7_500), ("house:gaming:EUR", AccountType.HouseGaming, 2_500L, null) },
            payload.Postings.Select(p => (p.AccountCode, p.AccountType, p.AmountMinor, p.BalanceAfterMinor)));
    }

    [Fact]
    public async Task Rejected_commands_and_replays_write_nothing()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(1_000)));
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(1_000)));
        await Assert.ThrowsAsync<InsufficientFundsException>(() => _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(5_000), "r-1")));

        Assert.Single(await OutboxAsync());
    }

    [Fact]
    public async Task The_dispatcher_publishes_in_commit_order_and_marks_messages_processed()
    {
        await PostSomeEntriesAsync(5);
        var written = (await OutboxAsync()).Select(m => m.MessageId).ToList();

        var delivered = await DispatchAsync();

        Assert.Equal(5, delivered);
        Assert.Equal(written, _publisher.Delivered.Select(m => m.MessageId));
        Assert.All(await OutboxAsync(), m => Assert.NotNull(m.ProcessedAt));
        Assert.Equal(0, await DispatchAsync());
        Assert.Equal(5, _publisher.Delivered.Count);
    }

    [Fact]
    public async Task A_failed_publish_leaves_the_message_pending_and_it_is_delivered_on_a_later_pass()
    {
        await PostSomeEntriesAsync(5);
        var written = (await OutboxAsync()).Select(m => m.MessageId).ToList();
        _publisher.FailWhen = m => m.MessageId == written[2] ? new InvalidOperationException("broker unavailable") : null;

        Assert.Equal(2, await DispatchAsync());

        var afterFailure = await OutboxAsync();
        Assert.Equal(2, afterFailure.Count(m => m.ProcessedAt is not null));
        var failed = afterFailure.Single(m => m.MessageId == written[2]);
        Assert.Null(failed.ProcessedAt);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("broker unavailable", failed.LastError);

        // The broker recovers: the rest goes out, still in order, and nothing is skipped.
        _publisher.FailWhen = null;
        Assert.Equal(3, await DispatchAsync());
        Assert.Equal(written, _publisher.Delivered.Select(m => m.MessageId));
        Assert.All(await OutboxAsync(), m => Assert.NotNull(m.ProcessedAt));
    }

    [Fact]
    public async Task Lag_and_backlog_are_reported_while_messages_wait()
    {
        await PostSomeEntriesAsync(3);
        _clock.Advance(TimeSpan.FromSeconds(45));
        _publisher.FailWhen = _ => new InvalidOperationException("broker unavailable");

        await DispatchAsync();

        var state = _ledger.Get<OutboxState>();
        Assert.Equal(3, state.Pending);
        Assert.Equal(TimeSpan.FromSeconds(45), state.Lag);
        Assert.Equal(HealthStatus.Degraded, (await CheckHealthAsync()).Status);

        _publisher.FailWhen = null;
        await DispatchAsync();

        Assert.Equal(0, state.Pending);
        Assert.Equal(TimeSpan.Zero, state.Lag);
        Assert.Equal(HealthStatus.Healthy, (await CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task Messages_carry_the_trace_context_of_the_request_that_wrote_them()
    {
        using var source = new System.Diagnostics.ActivitySource("test");
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using (var request = source.StartActivity("POST /v1/deposits"))
        {
            await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(1_000)));
            var message = Assert.Single(await OutboxAsync());
            Assert.Contains(request!.TraceId.ToString(), message.TraceParent, StringComparison.Ordinal);
        }

        await DispatchAsync();
        var published = Assert.Single(_publisher.Delivered);
        var original = Assert.Single(await OutboxAsync());
        Assert.Equal(original.TraceParent![..35], published.TraceParent![..35]);
    }

    private Task<int> DispatchAsync() => _ledger.Get<OutboxDispatcher>().DispatchBatchAsync(TestContext.Current.CancellationToken);

    private Task<HealthCheckResult> CheckHealthAsync() =>
        ActivatorUtilities.CreateInstance<OutboxHealthCheck>(_ledger.Provider).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private Task<List<OutboxMessage>> OutboxAsync() =>
        _ledger.QueryAsync(db => db.OutboxMessages.OrderBy(m => m.Id).ToListAsync());

    private async Task PostSomeEntriesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _ledger.SendAsync(new SettleDeposit($"dep-{i}", "northwind-pay", $"p-{i % 2}", Eur(100 + i)));
        }
    }

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
