using Ledgerline.Api.Contracts;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Queries;
using Ledgerline.Domain;

namespace Ledgerline.Api.Endpoints;

/// <summary>Read side: each endpoint calls one query handler, which runs one or two SQL statements.</summary>
internal static class QueryEndpoints
{
    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/players/{playerId}/wallets", async (
                string playerId,
                IQueryHandler<GetPlayerWallets, IReadOnlyList<AccountView>> handler,
                CancellationToken ct) =>
            {
                ExternalId.Require(playerId, "playerId");
                var wallets = await handler.HandleAsync(new GetPlayerWallets(playerId), ct);
                return TypedResults.Ok(new PlayerWalletsResponse(playerId, [.. wallets.Select(AccountResponse.From)]));
            })
            .WithName("GetPlayerWallets")
            .WithTags("Wallets")
            .WithSummary("Balances of every wallet a player has, per currency")
            .ProducesValidationProblem();

        var accounts = v1.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapGet("/{accountId:guid}", async (Guid accountId, IQueryHandler<GetAccount, AccountView?> handler, CancellationToken ct) =>
                TypedResults.Ok(AccountResponse.From(
                    await handler.HandleAsync(new GetAccount(accountId), ct) ?? throw new LedgerNotFoundException("Account", accountId.ToString()))))
            .WithName("GetAccount")
            .WithSummary("One account with its current balance")
            .ProducesProblem(StatusCodes.Status404NotFound);

        accounts.MapGet("/{accountId:guid}/statement", async (
                Guid accountId,
                int? limit,
                string? cursor,
                IQueryHandler<GetStatement, StatementPage?> handler,
                CancellationToken ct) =>
            {
                var page = await handler.HandleAsync(new GetStatement(accountId, limit ?? GetStatement.DefaultLimit, cursor), ct)
                    ?? throw new LedgerNotFoundException("Account", accountId.ToString());
                return TypedResults.Ok(StatementResponse.From(page));
            })
            .WithName("GetStatement")
            .WithSummary("Account statement, newest first, with keyset pagination (pass nextCursor back as cursor)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapGet("/entries/{entryId:guid}", async (Guid entryId, IQueryHandler<GetEntry, EntryView?> handler, CancellationToken ct) =>
                TypedResults.Ok(EntryResponse.From(
                    await handler.HandleAsync(new GetEntry(entryId), ct) ?? throw new LedgerNotFoundException("Entry", entryId.ToString()))))
            .WithName("GetEntry")
            .WithTags("Journal")
            .WithSummary("A journal entry with all of its postings")
            .ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapGet("/withdrawals/pending", async (int? limit, IQueryHandler<GetPendingWithdrawals, IReadOnlyList<PendingWithdrawalView>> handler, CancellationToken ct) =>
            {
                var pending = await handler.HandleAsync(new GetPendingWithdrawals(limit ?? GetPendingWithdrawals.DefaultLimit), ct);
                return TypedResults.Ok(pending
                    .Select(w => new PendingWithdrawalResponse(w.WithdrawalId, w.PlayerId, w.Provider, MoneyResponse.From(w.AmountMinor, w.Currency), w.RequestedAt))
                    .ToList());
            })
            .WithName("GetPendingWithdrawals")
            .WithTags("Payments")
            .WithSummary("Open withdrawal holds, oldest first")
            .ProducesValidationProblem();

        v1.MapGet("/ledger/trial-balance", async (IQueryHandler<GetTrialBalance, IReadOnlyList<TrialBalanceLine>> handler, CancellationToken ct) =>
                TypedResults.Ok(TrialBalanceResponse.From(await handler.HandleAsync(new GetTrialBalance(), ct))))
            .WithName("GetTrialBalance")
            .WithTags("Journal")
            .WithSummary("Net balance per account type and currency; every currency must net to zero");

        return v1;
    }
}
