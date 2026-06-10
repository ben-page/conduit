using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Fluent builder for configuring durable messaging (outbox, inbox, dead letter queue).
/// The persistence engine is configured separately on the <see cref="Conduit.Configuration.ConduitBuilder"/>
/// (e.g. <c>conduit.UseSqlite(...)</c>) and shared by all features via DI
/// (<c>IConduitConnectionFactory</c> + <c>ISqlDialect</c>).
/// </summary>
public sealed class DurabilityBuilder
{
    internal IServiceCollection Services { get; }

    internal DurabilityBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>Adds the transactional outbox.</summary>
    public DurabilityBuilder AddOutbox(Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IOutboxStore, DbConnectionOutboxStore>();

        this.Services.AddScoped<OutboxCapture>();
        this.Services.AddScoped<IOutboundInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());
        this.Services.AddScoped<IOutboxFlusher>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());

        this.Services.TryAddTransient(typeof(OutboxBehavior<,>));

        this.Services.AddHostedService<OutboxRelay>();

        this.Services.AddSingleton(Options.Create(options));
        return this;
    }

    /// <summary>Adds the inbox (idempotent message processing).</summary>
    public DurabilityBuilder AddInbox(Action<InboxOptions>? configure = null)
    {
        var options = new InboxOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IInboxStore, DbConnectionInboxStore>();
        this.Services.AddScoped<IInboundInterceptor, InboxInterceptor>();
        this.Services.AddHostedService<InboxCleanup>();
        this.Services.AddSingleton(Options.Create(options));

        return this;
    }

    /// <summary>Adds the dead letter queue (capture failed messages, browse, replay).</summary>
    public DurabilityBuilder AddDeadLetterQueue(Action<DeadLetterQueueOptions>? configure = null)
    {
        var options = new DeadLetterQueueOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IDeadLetterStore, DbDeadLetterStore>();
        this.Services.TryAddScoped<IDeadLetters, Runtime.DeadLetterQueue.DeadLetters>();
        this.Services.AddScoped<IInboundErrorInterceptor, DeadLetterErrorInterceptor>();
        this.Services.AddSingleton(Options.Create(options));

        return this;
    }
}
