using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Queries;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Outbox;
using Ledgerline.Infrastructure.Persistence;
using Ledgerline.Infrastructure.Projections;
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
        services.AddSingleton<IQueryHandler<GetRoundSummary, RoundSummaryView?>, GetRoundSummaryHandler>();

        services.AddLedgerMessaging(configuration);
        return services;
    }

    private static void AddLedgerMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.Transport != MessageTransport.Kafka || !string.IsNullOrWhiteSpace(o.Kafka.BootstrapServers),
                "Messaging:Kafka:BootstrapServers is required when Messaging:Transport is Kafka.")
            .ValidateOnStart();
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<OutboxState>();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<LedgerMessageHandler>();
        services.AddScoped<RoundSummaryProjector>();

        // The transport decides which hosted services exist, so it is read once, at registration.
        var messaging = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();
        var outbox = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();

        if (messaging.Transport == MessageTransport.Kafka)
        {
            services.AddSingleton<IMessagePublisher, KafkaMessagePublisher>();
            if (messaging.Kafka.CreateTopic)
            {
                services.AddHostedService<KafkaTopicInitializer>();
            }

            if (messaging.EnableRoundProjection)
            {
                services.AddHostedService<KafkaLedgerConsumer>();
            }
        }
        else
        {
            services.AddSingleton<InMemoryMessageBus>();
            services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<InMemoryMessageBus>());
            if (messaging.EnableRoundProjection)
            {
                services.AddHostedService<InMemoryLedgerConsumer>();
            }
        }

        if (outbox.Enabled)
        {
            services.AddHostedService<OutboxDispatcherService>();
        }
    }
}
