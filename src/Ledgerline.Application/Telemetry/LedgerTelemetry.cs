using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ledgerline.Application.Telemetry;

/// <summary>Names shared by tracing and metrics registration.</summary>
public static class LedgerTelemetry
{
    public const string SourceName = "Ledgerline";

    public static readonly ActivitySource ActivitySource = new(SourceName);
}

/// <summary>
/// Business metrics for the write side. Created through <see cref="IMeterFactory"/> so tests can
/// observe them with a <c>MetricCollector</c> and every host gets its own meter instance.
/// </summary>
public sealed class LedgerMetrics
{
    public const string MeterName = LedgerTelemetry.SourceName;

    private readonly Counter<long> _commands;
    private readonly Counter<long> _entries;
    private readonly Counter<long> _postings;
    private readonly Counter<long> _conflictRetries;
    private readonly Histogram<double> _commandDuration;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _commands = meter.CreateCounter<long>("ledger.commands", "{command}", "Commands handled, by command and outcome.");
        _entries = meter.CreateCounter<long>("ledger.entries.posted", "{entry}", "Journal entries posted (replays excluded).");
        _postings = meter.CreateCounter<long>("ledger.postings", "{posting}", "Postings written. Its rate is postings per second.");
        _conflictRetries = meter.CreateCounter<long>("ledger.concurrency.retries", "{retry}", "Commands retried after losing an optimistic-concurrency race.");
        _commandDuration = meter.CreateHistogram<double>("ledger.command.duration", "s", "End-to-end command time, retries included.");
    }

    public void CommandHandled(string command, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "ledger.command", command }, { "ledger.outcome", outcome } };
        _commands.Add(1, tags);
        _commandDuration.Record(elapsed.TotalSeconds, tags);
    }

    public void EntryPosted(PostingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var type = new KeyValuePair<string, object?>("ledger.entry_type", result.Type.ToString());
        _entries.Add(1, type);
        _postings.Add(result.Postings.Count, type);
    }

    public void ConflictRetried(string command) =>
        _conflictRetries.Add(1, new KeyValuePair<string, object?>("ledger.command", command));
}
