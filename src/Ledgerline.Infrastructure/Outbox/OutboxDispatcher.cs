using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ledgerline.Application.Telemetry;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledgerline.Infrastructure.Outbox;

/// <summary>
/// Relays committed outbox rows to the message transport, oldest first, and marks them processed.
/// Delivery is at-least-once: a crash between "broker acknowledged" and "row marked" re-sends the
/// message on the next pass, which is why consumers deduplicate on the message id.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IMessagePublisher publisher,
    OutboxState state,
    OutboxMetrics metrics,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Arbitrary but fixed: only one dispatcher across all instances holds this PostgreSQL advisory lock at a time.</summary>
    private const long LeaderLockKey = 0x4C_45_44_47_45_52; // "LEDGER"

    /// <summary>Publishes up to one batch. Returns how many messages were delivered.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        // On PostgreSQL a transaction-scoped advisory lock elects one active dispatcher, which keeps
        // per-key order across replicas. SQLite is single-process, so there is nothing to coordinate.
        await using var transaction = db.Database.IsNpgsql() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null && !await TryBecomeLeaderAsync(db, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var batch = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.Id)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken);

        var delivered = batch.Count == 0 ? 0 : await PublishAsync(batch, cancellationToken);
        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await CommitAsync(transaction, cancellationToken);
        await RefreshBacklogAsync(db, cancellationToken);
        return delivered;
    }

    private async Task<int> PublishAsync(List<OutboxMessage> batch, CancellationToken cancellationToken)
    {
        // One producer span per message, parented on the request that wrote it.
        var activities = batch.Select(StartPublishActivity).ToList();
        var outbound = batch.Select((m, i) => new OutboundMessage(
            m.MessageId, m.Topic, m.MessageKey, m.MessageType, m.Payload, activities[i]?.Id ?? m.TraceParent, m.OccurredAt)).ToList();

        PublishOutcome outcome;
        try
        {
            outcome = await publisher.PublishAsync(outbound, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = new PublishOutcome(0, ex);
        }

        var now = clock.GetUtcNow();
        for (var i = 0; i < batch.Count; i++)
        {
            if (i < outcome.Delivered)
            {
                batch[i].MarkProcessed(now);
                metrics.Published(batch[i].Topic, now - batch[i].OccurredAt);
            }

            activities[i]?.SetStatus(i < outcome.Delivered ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            activities[i]?.Dispose();
        }

        if (outcome.Error is not null && outcome.Delivered < batch.Count)
        {
            var failed = batch[outcome.Delivered];
            failed.RecordFailure(outcome.Error.Message);
            metrics.PublishFailed(failed.Topic);
            LogPublishFailed(logger, outcome.Error, failed.MessageId, failed.Attempts);
        }

        return outcome.Delivered;
    }

    private async Task RefreshBacklogAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var pending = db.OutboxMessages.Where(m => m.ProcessedAt == null);
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0
            ? null
            : await pending.OrderBy(m => m.Id).Select(m => (DateTimeOffset?)m.OccurredAt).FirstOrDefaultAsync(cancellationToken);
        state.Update(count, oldest is null ? TimeSpan.Zero : clock.GetUtcNow() - oldest.Value);
    }

    private static async Task<bool> TryBecomeLeaderAsync(LedgerDbContext db, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LeaderLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static Activity? StartPublishActivity(OutboxMessage message)
    {
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        var activity = LedgerTelemetry.ActivitySource.StartActivity($"{message.Topic} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", message.Topic);
        activity?.SetTag("messaging.message.id", message.MessageId.ToString());
        return activity;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Publishing outbox message {MessageId} failed (attempt {Attempts}); it stays pending and will be retried")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid messageId, int attempts);
}

/// <summary>Polls the outbox until shutdown. Drains back-to-back while batches come back full.</summary>
public sealed partial class OutboxDispatcherService(
    OutboxDispatcher dispatcher,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                delivered = await dispatcher.DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database briefly unavailable, for example. Never let the relay die; just try again.
                LogDispatchFailed(logger, ex);
            }

            if (delivered < options.Value.BatchSize)
            {
                try
                {
                    await Task.Delay(options.Value.PollingInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Outbox dispatch pass failed")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);
}

/// <summary>Last observed outbox backlog, shared by the metrics gauges and the readiness check.</summary>
public sealed class OutboxState
{
    private long _pending;
    private long _lagTicks;

    public long Pending => Interlocked.Read(ref _pending);

    public TimeSpan Lag => TimeSpan.FromTicks(Interlocked.Read(ref _lagTicks));

    public void Update(long pending, TimeSpan lag)
    {
        Interlocked.Exchange(ref _pending, pending);
        Interlocked.Exchange(ref _lagTicks, Math.Max(0, lag.Ticks));
    }
}

public sealed class OutboxMetrics
{
    private readonly Counter<long> _published;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _delay;

    public OutboxMetrics(IMeterFactory meterFactory, OutboxState state)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(state);
        var meter = meterFactory.Create(LedgerMetrics.MeterName);
        _published = meter.CreateCounter<long>("ledger.outbox.published", "{message}", "Outbox messages acknowledged by the transport.");
        _failures = meter.CreateCounter<long>("ledger.outbox.publish_failures", "{message}", "Publish attempts that failed and will be retried.");
        _delay = meter.CreateHistogram<double>("ledger.outbox.delivery_delay", "s", "Time from commit to broker acknowledgement.");
        meter.CreateObservableGauge("ledger.outbox.pending", () => state.Pending, "{message}", "Committed messages not yet published.");
        meter.CreateObservableGauge("ledger.outbox.lag", () => state.Lag.TotalSeconds, "s", "Age of the oldest unpublished message.");
    }

    public void Published(string topic, TimeSpan delay)
    {
        var tag = new KeyValuePair<string, object?>("messaging.destination.name", topic);
        _published.Add(1, tag);
        _delay.Record(Math.Max(0, delay.TotalSeconds), tag);
    }

    public void PublishFailed(string topic) =>
        _failures.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));
}

/// <summary>Degraded, not Unhealthy: writes still work while the broker is away, they just queue up.</summary>
public sealed class OutboxHealthCheck(OutboxState state, IOptions<OutboxOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["pending"] = state.Pending, ["lagSeconds"] = Math.Round(state.Lag.TotalSeconds, 1) };
        return Task.FromResult(state.Lag > options.Value.LagWarningThreshold
            ? HealthCheckResult.Degraded($"Oldest unpublished message is {state.Lag.TotalSeconds:0}s old.", data: data)
            : HealthCheckResult.Healthy("Outbox is draining.", data));
    }
}
