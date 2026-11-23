using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Telemetry;
using Ledgerline.Domain;
using Ledgerline.Domain.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Ledgerline.Application.Dispatching;

public sealed class ConcurrencyOptions
{
    public const string SectionName = "Ledger:Concurrency";

    /// <summary>How many times a command is re-run after losing a race. Bounded so a hot wallet sheds load instead of piling up.</summary>
    [Range(1, 20)]
    public int MaxRetries { get; set; } = 5;

    /// <summary>First backoff delay; doubles per attempt, with jitter so racing writers spread out.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:00:05")]
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(10);
}

/// <summary>
/// Runs a command handler inside a bounded retry loop. Each attempt gets a fresh DI scope, and with it
/// a fresh unit of work, so a retry re-reads current balances instead of reusing stale tracked state.
/// Retrying is safe because every command is idempotent: an attempt that actually committed is found
/// by its idempotency key and returned as a replay.
/// </summary>
public sealed partial class CommandDispatcher
{
    private static readonly ResiliencePropertyKey<string> CommandName = new("ledger.command");

    private readonly IServiceScopeFactory _scopes;
    private readonly LedgerMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly ResiliencePipeline _retry;
    private readonly int _maxAttempts;

    public CommandDispatcher(
        IServiceScopeFactory scopes,
        IOptions<ConcurrencyOptions> options,
        LedgerMetrics metrics,
        TimeProvider time,
        ILogger<CommandDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _metrics = metrics;
        _time = time;
        _logger = logger;
        _maxAttempts = options.Value.MaxRetries + 1;
        _retry = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientWriteConflictException>(),
                MaxRetryAttempts = options.Value.MaxRetries,
                Delay = options.Value.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var command = args.Context.Properties.GetValue(CommandName, "unknown");
                    _metrics.ConflictRetried(command);
                    LogConflictRetry(_logger, command, args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            })
            .Build();
    }

    public async Task<PostingResult> SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : class, ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = typeof(TCommand).Name;
        using var activity = LedgerTelemetry.ActivitySource.StartActivity($"ledger.command {name}");
        activity?.SetTag("ledger.command", name);
        activity?.SetTag("ledger.idempotency_key", command.IdempotencyKey);

        var started = _time.GetTimestamp();
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(CommandName, name);
        var outcome = "error";
        try
        {
            var result = await _retry.ExecuteAsync(
                static async (ctx, state) =>
                {
                    await using var scope = state.Scopes.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>();
                    return await handler.HandleAsync(state.Command, ctx.CancellationToken);
                },
                context,
                (Scopes: _scopes, Command: command));

            outcome = result.Replayed ? "replayed" : "posted";
            activity?.SetTag("ledger.entry_id", result.EntryId);
            activity?.SetTag("ledger.replayed", result.Replayed);
            if (!result.Replayed)
            {
                _metrics.EntryPosted(result);
                LogPosted(_logger, result.Type, result.EntryId, command.IdempotencyKey);
            }

            return result;
        }
        catch (TransientWriteConflictException ex)
        {
            outcome = "write-conflict";
            LogRetriesExhausted(_logger, name, _maxAttempts, command.IdempotencyKey);
            throw new WriteConflictException(_maxAttempts, ex);
        }
        catch (LedgerException ex)
        {
            outcome = ex.Code;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Code);
            throw;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
            _metrics.CommandHandled(name, outcome, _time.GetElapsedTime(started));
            activity?.SetTag("ledger.outcome", outcome);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Posted {EntryType} entry {EntryId} for {IdempotencyKey}")]
    private static partial void LogPosted(ILogger logger, EntryType entryType, Guid entryId, string idempotencyKey);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "{Command} lost a concurrent write; attempt {Attempt} in {DelayMs:0.0} ms")]
    private static partial void LogConflictRetry(ILogger logger, string command, int attempt, double delayMs);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "{Command} gave up after {Attempts} attempts on {IdempotencyKey}")]
    private static partial void LogRetriesExhausted(ILogger logger, string command, int attempts, string idempotencyKey);
}
