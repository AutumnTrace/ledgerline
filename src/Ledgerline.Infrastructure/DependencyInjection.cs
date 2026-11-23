using Ledgerline.Application.Abstractions;
using Ledgerline.Infrastructure.Persistence;
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
        return services;
    }
}
