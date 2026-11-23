using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Commands;
using Ledgerline.Application.Dispatching;
using Ledgerline.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledgerline.Application;

public static class DependencyInjection
{
    /// <summary>Registers the dispatcher and every command handler. Handlers are listed explicitly: no assembly scanning.</summary>
    public static IServiceCollection AddLedgerApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ConcurrencyOptions>()
            .BindConfiguration(ConcurrencyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<LedgerMetrics>();
        services.AddSingleton<CommandDispatcher>();

        services.AddScoped<ICommandHandler<SettleDeposit>, SettleDepositHandler>();
        services.AddScoped<ICommandHandler<PlaceBet>, PlaceBetHandler>();
        services.AddScoped<ICommandHandler<CreditWin>, CreditWinHandler>();
        services.AddScoped<ICommandHandler<RollbackBet>, RollbackBetHandler>();
        services.AddScoped<ICommandHandler<RequestWithdrawal>, RequestWithdrawalHandler>();
        services.AddScoped<ICommandHandler<CompleteWithdrawal>, CompleteWithdrawalHandler>();
        services.AddScoped<ICommandHandler<ReverseWithdrawal>, ReverseWithdrawalHandler>();
        services.AddScoped<ICommandHandler<GrantBonus>, GrantBonusHandler>();
        services.AddScoped<ICommandHandler<TransferBetweenWallets>, TransferBetweenWalletsHandler>();

        return services;
    }
}
