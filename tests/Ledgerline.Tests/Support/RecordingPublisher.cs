using Ledgerline.Infrastructure.Messaging;

namespace Ledgerline.Tests.Support;

/// <summary>A transport that records what it was given and can be told to fail on chosen messages.</summary>
internal sealed class RecordingPublisher : IMessagePublisher
{
    private readonly List<OutboundMessage> _delivered = [];

    public IReadOnlyList<OutboundMessage> Delivered => _delivered;

    /// <summary>Return an exception to make the broker reject that message (and, by ordering, the rest of the batch).</summary>
    public Func<OutboundMessage, Exception?>? FailWhen { get; set; }

    public Task<PublishOutcome> PublishAsync(IReadOnlyList<OutboundMessage> batch, CancellationToken cancellationToken)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (FailWhen?.Invoke(batch[i]) is { } error)
            {
                return Task.FromResult(new PublishOutcome(i, error));
            }

            _delivered.Add(batch[i]);
        }

        return Task.FromResult(new PublishOutcome(batch.Count, null));
    }
}
