using Conduit.Configuration;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Inbox;

/// <summary>
/// Extension methods for registering the inbox.
/// </summary>
public static class InboxServiceExtensions
{
    /// <summary>
    /// Adds the inbox (idempotent message processing) to the Conduit configuration.
    /// </summary>
    public static ConduitBuilder AddInbox(
        this ConduitBuilder builder,
        Action<InboxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var inboxBuilder = new InboxBuilder(builder.Services);
        configure(inboxBuilder);

        if (!inboxBuilder.StoreConfigured)
        {
            throw new InvalidOperationException(
                "Inbox store not configured. Call UseSqlite(), UseSqlServer<T>(), UsePostgres<T>(), or UseMySql<T>().");
        }

        builder.Services.TryAddScoped<IInboxStore, DbConnectionInboxStore>();
        builder.Services.AddScoped<IInboundInterceptor, InboxInterceptor>();
        builder.Services.AddHostedService<InboxCleanup>();
        builder.Services.AddSingleton(Options.Create(inboxBuilder.Options));

        return builder;
    }
}
