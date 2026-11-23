using Ledgerline.Application.Abstractions;
using Ledgerline.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerline.Api.Errors;

/// <summary>
/// Maps business rule violations to RFC 9457 problem details. Each problem carries a stable <c>code</c>
/// and a <c>type</c> URI that points at its documentation. Anything that is not a
/// <see cref="LedgerException"/> falls through to the default 500 handler, which never leaks details.
/// </summary>
internal sealed partial class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    private const string DocsBase = "https://github.com/AutumnTrace/ledgerline/blob/main/docs/errors.md#";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is BadHttpRequestException badRequest)
        {
            // Malformed JSON or a body that cannot bind: the client's fault, never a 500.
            httpContext.Response.StatusCode = badRequest.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = { Status = badRequest.StatusCode, Title = "The request body could not be read.", Type = DocsBase + "malformed-request" },
                Exception = exception,
            });
        }

        if (exception is not LedgerException ledger)
        {
            return false;
        }

        var (status, title) = ledger switch
        {
            LedgerValidationException => (StatusCodes.Status400BadRequest, "The request is invalid."),
            CurrencyMismatchException => (StatusCodes.Status400BadRequest, "Currency mismatch."),
            LedgerNotFoundException => (StatusCodes.Status404NotFound, "Not found."),
            IdempotencyConflictException => (StatusCodes.Status409Conflict, "Idempotency key reused with a different request."),
            InvalidLedgerStateException => (StatusCodes.Status409Conflict, "The operation is not allowed in the current state."),
            WriteConflictException => (StatusCodes.Status409Conflict, "Concurrent update, retry with the same transaction id."),
            InsufficientFundsException => (StatusCodes.Status422UnprocessableEntity, "Insufficient funds."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The ledger rejected the request."),
        };

        ProblemDetails problem = ledger is LedgerValidationException invalid
            ? new HttpValidationProblemDetails(new Dictionary<string, string[]> { [invalid.Field] = [invalid.Message] })
            : new ProblemDetails();
        problem.Status = status;
        problem.Title = title;
        problem.Detail = ledger.Message;
        problem.Type = DocsBase + ledger.Code;
        problem.Extensions["code"] = ledger.Code;

        if (ledger is InsufficientFundsException funds)
        {
            problem.Extensions["accountCode"] = funds.AccountCode;
            problem.Extensions["availableMinor"] = funds.Available.Minor;
            problem.Extensions["requestedMinor"] = funds.Requested.Minor;
        }

        if (ledger is WriteConflictException)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        LogRejected(logger, ledger.Code, status, ledger.Message);
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Rejected with {Code} ({Status}): {Detail}")]
    private static partial void LogRejected(ILogger logger, string code, int status, string detail);
}
