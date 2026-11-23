namespace Ledgerline.Domain;

/// <summary>
/// Base type for every business rule violation. <see cref="Code"/> is a stable, kebab-case
/// identifier that the API exposes in problem details, so clients can branch on it.
/// </summary>
public abstract class LedgerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Input that can never be valid, regardless of ledger state.</summary>
public sealed class LedgerValidationException(string field, string message) : LedgerException("validation-failed", message)
{
    public string Field { get; } = field;
}

/// <summary>A player wallet would go below zero.</summary>
public sealed class InsufficientFundsException(string accountCode, Money available, Money requested)
    : LedgerException("insufficient-funds", $"Account {accountCode} has {available} available, {requested} was requested.")
{
    public string AccountCode { get; } = accountCode;

    public Money Available { get; } = available;

    public Money Requested { get; } = requested;
}

/// <summary>Postings that do not sum to zero per currency. Indicates a programming error, never user input.</summary>
public sealed class UnbalancedEntryException(string message) : LedgerException("unbalanced-entry", message);

public sealed class CurrencyMismatchException(Currency expected, Currency actual)
    : LedgerException("currency-mismatch", $"Expected {expected.Code} but got {actual.Code}.")
{
    public Currency Expected { get; } = expected;

    public Currency Actual { get; } = actual;
}

/// <summary>The operation is valid in general but not in the current state (e.g. completing a reversed withdrawal).</summary>
public sealed class InvalidLedgerStateException(string message) : LedgerException("invalid-state", message);
