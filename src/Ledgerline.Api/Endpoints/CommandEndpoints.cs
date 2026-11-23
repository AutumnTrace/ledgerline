using Ledgerline.Api.Contracts;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Application.Dispatching;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledgerline.Api.Endpoints;

/// <summary>
/// Money-moving endpoints. Each one is idempotent on the caller's transaction id: the first call returns
/// <c>201 Created</c>, a replay returns <c>200 OK</c> with the original body and <c>Idempotent-Replayed: true</c>.
/// </summary>
internal static class CommandEndpoints
{
    public const string ReplayHeader = "Idempotent-Replayed";

    public static RouteGroupBuilder MapCommandEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/deposits", (DepositRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new SettleDeposit(r.TransactionId!, r.Provider!, r.PlayerId!, Limits.Money(r.AmountMinor, r.Currency)), http, ct))
            .WithName("SettleDeposit")
            .WithTags("Payments")
            .WithSummary("Settle a deposit confirmed by a payment provider")
            .ProducesLedgerProblems();

        var wallet = v1.MapGroup("/wallet").WithTags("Seamless wallet");

        wallet.MapPost("/bets", (GameTransactionRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new PlaceBet(r.TransactionId!, r.PlayerId!, Limits.Money(r.AmountMinor, r.Currency), r.RoundId!), http, ct))
            .WithName("PlaceBet")
            .WithSummary("Debit a bet from the player's cash wallet")
            .ProducesLedgerProblems();

        wallet.MapPost("/wins", (GameTransactionRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new CreditWin(r.TransactionId!, r.PlayerId!, Limits.Money(r.AmountMinor, r.Currency), r.RoundId!), http, ct))
            .WithName("CreditWin")
            .WithSummary("Credit a win to the player's cash wallet")
            .ProducesLedgerProblems();

        wallet.MapPost("/rollbacks", (RollbackRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new RollbackBet(r.TransactionId!, r.PlayerId!, r.OriginalTransactionId!), http, ct))
            .WithName("RollbackBet")
            .WithSummary("Cancel a bet by posting its exact opposite")
            .ProducesLedgerProblems();

        var withdrawals = v1.MapGroup("/withdrawals").WithTags("Payments");

        withdrawals.MapPost("/", (WithdrawalRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new RequestWithdrawal(r.WithdrawalId!, r.Provider!, r.PlayerId!, Limits.Money(r.AmountMinor, r.Currency)), http, ct))
            .WithName("RequestWithdrawal")
            .WithSummary("Reserve a payout: cash moves into PSP suspense until the provider confirms")
            .ProducesLedgerProblems();

        withdrawals.MapPost("/{withdrawalId}/complete", (string withdrawalId, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new CompleteWithdrawal(withdrawalId), http, ct))
            .WithName("CompleteWithdrawal")
            .WithSummary("The provider paid out: release the hold to PSP clearing")
            .ProducesLedgerProblems();

        withdrawals.MapPost("/{withdrawalId}/reverse", (string withdrawalId, ReverseWithdrawalRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new ReverseWithdrawal(withdrawalId, r.Reason!), http, ct))
            .WithName("ReverseWithdrawal")
            .WithSummary("The payout failed: return the hold to the player's cash wallet")
            .ProducesLedgerProblems();

        v1.MapPost("/bonuses", (BonusRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new GrantBonus(r.TransactionId!, r.PlayerId!, Limits.Money(r.AmountMinor, r.Currency)), http, ct))
            .WithName("GrantBonus")
            .WithTags("Wallets")
            .WithSummary("Grant bonus money from the operator's bonus pool")
            .ProducesLedgerProblems();

        v1.MapPost("/transfers", (TransferRequest r, CommandDispatcher d, HttpContext http, CancellationToken ct) =>
                SendAsync(d, new TransferBetweenWallets(r.TransactionId!, r.PlayerId!, r.From!.Value, r.To!.Value, Limits.Money(r.AmountMinor, r.Currency)), http, ct))
            .WithName("TransferBetweenWallets")
            .WithTags("Wallets")
            .WithSummary("Move money between a player's cash and bonus wallets")
            .ProducesLedgerProblems();

        return v1;
    }

    private static async Task<Results<Created<PostingResponse>, Ok<PostingResponse>>> SendAsync<TCommand>(
        CommandDispatcher dispatcher,
        TCommand command,
        HttpContext http,
        CancellationToken cancellationToken)
        where TCommand : class, ICommand
    {
        var result = await dispatcher.SendAsync(command, cancellationToken);
        var body = PostingResponse.From(result);
        if (result.Replayed)
        {
            http.Response.Headers[ReplayHeader] = "true";
            return TypedResults.Ok(body);
        }

        return TypedResults.Created($"/v1/entries/{result.EntryId}", body);
    }

    private static RouteHandlerBuilder ProducesLedgerProblems(this RouteHandlerBuilder builder) => builder
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
