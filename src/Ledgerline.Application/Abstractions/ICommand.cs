namespace Ledgerline.Application.Abstractions;

/// <summary>
/// A request to move money. Every command is idempotent: executing it twice with the same
/// <see cref="IdempotencyKey"/> posts once and returns the original result the second time.
/// </summary>
public interface ICommand
{
    /// <summary>Unique per business operation, derived from the caller's transaction id (e.g. <c>wallet:tx-123</c>).</summary>
    string IdempotencyKey { get; }

    /// <summary>
    /// Canonical rendering of the fields that define the operation. A replay must produce the same
    /// fingerprint, otherwise the key is being reused for a different request and is rejected.
    /// </summary>
    string Fingerprint { get; }
}

public interface ICommandHandler<in TCommand>
    where TCommand : class, ICommand
{
    Task<PostingResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>A read-side query. Handlers live in the infrastructure layer and never touch the EF Core model.</summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : class
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
