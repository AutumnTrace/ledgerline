using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Queries;
using Ledgerline.Infrastructure.Persistence;
using Ledgerline.Infrastructure.ReadModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ledgerline.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLedgerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .PostConfigure(o => o.ConnectionString = configuration.GetConnectionString("Ledger") ?? o.ConnectionString)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContextPool<LedgerDbContext>((sp, builder) =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            DatabaseSetup.Configure(builder, options.Provider, options.ConnectionString);
        });

        services.AddScoped<ILedgerStore, EfLedgerStore>();

        // Read side: plain SQL over its own connections, no change tracking, no aggregates.
        services.AddSingleton<ReadConnectionFactory>();
        services.AddSingleton<IQueryHandler<GetPlayerWallets, IReadOnlyList<AccountView>>, GetPlayerWalletsHandler>();
        services.AddSingleton<IQueryHandler<GetAccount, AccountView?>, GetAccountHandler>();
        services.AddSingleton<IQueryHandler<GetStatement, StatementPage?>, GetStatementHandler>();
        services.AddSingleton<IQueryHandler<GetEntry, EntryView?>, GetEntryHandler>();
        services.AddSingleton<IQueryHandler<GetPendingWithdrawals, IReadOnlyList<PendingWithdrawalView>>, GetPendingWithdrawalsHandler>();
        services.AddSingleton<IQueryHandler<GetTrialBalance, IReadOnlyList<TrialBalanceLine>>, GetTrialBalanceHandler>();

        return services;
    }
}
