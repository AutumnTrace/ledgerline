using Ledgerline.Domain;

namespace Ledgerline.Application.Abstractions;

/// <summary>The idempotency key was already used for a request with a different payload.</summary>
public sealed class IdempotencyConflictException(string idempotencyKey)
    : LedgerException("idempotency-key-reused", $"Idempotency key '{idempotencyKey}' was already used for a different request.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

public sealed class LedgerNotFoundException(string resource, string id)
    : LedgerException("not-found", $"{resource} '{id}' was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>Every retry lost a race against a concurrent write. Safe for the caller to retry with the same key.</summary>
public sealed class WriteConflictException(int attempts, Exception innerException)
    : LedgerException("write-conflict", $"The write lost a concurrent update {attempts} times in a row; retry with the same transaction id.")
{
    public int Attempts { get; } = attempts;

    public Exception Cause { get; } = innerException;
}

/// <summary>
/// Raised by the store when the database rejected a write because another transaction got there first
/// (stale optimistic-concurrency token, duplicate idempotency key, serialization failure, deadlock).
/// Not a business error: the dispatcher re-runs the command in a fresh unit of work.
/// </summary>
public sealed class TransientWriteConflictException(string message, Exception innerException) : Exception(message, innerException);
