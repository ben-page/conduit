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
    /// <exception cref="OptionsValidationException">
    /// <see cref="OutboxOptions.PersistedProperties"/> lists an unsupported type or an empty key, or
    /// <see cref="OutboxOptions.InitialBackoff"/> is negative or above <see cref="OutboxOptions.MaxBackoff"/>.
    /// </exception>
    public DurabilityBuilder AddOutbox(Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);
        options.Validate();

        this.Services.TryAddScoped<IOutboxStore, DbConnectionOutboxStore>();

        // Retrying a failed entry is a row update, so a capture-only host can hand one back to the relay that owns it.
        this.Services.TryAddScoped<IOutbox, OutboxOperations>();

        this.Services.AddScoped<OutboxCapture>();
        this.Services.AddScoped<IOutboundInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());

        // Writing (unlike delivery) happens in capture-only mode too: rows go on the caller's transaction.
        this.Services.AddScoped<IOutboxPendingWriter, OutboxPendingWriter>();
        if (options.CaptureOnly)
        {
            // Another process's relay owns delivery (OutboxOptions.CaptureOnly's remarks).
            this.Services.AddSingleton<IOutboxFlusher, CaptureOnlyOutboxFlusher>();
        }
        else
        {
            this.Services.AddScoped<IOutboxFlusher>(
                serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());
            this.Services.AddHostedService<OutboxRelay>();
        }

        this.Services.TryAddTransient(typeof(OutboxBehavior<,>));

        this.Services.AddSingleton(Options.Create(options));
        return this;
    }

    /// <summary>
    /// Adds the inbox (idempotent message processing). A broker dispatch that fails releases its record, so
    /// the broker's redelivery, or a dead-letter replay, runs instead of being discarded as a duplicate.
    /// </summary>
    public DurabilityBuilder AddInbox(Action<InboxOptions>? configure = null)
    {
        var options = new InboxOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IInboxStore, DbConnectionInboxStore>();

        // One gate per dispatch scope, shared by the error interceptor: it releases the record that gate made.
        this.Services.AddScoped<InboxInterceptor>();
        this.Services.AddScoped<IInboundInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<InboxInterceptor>());
        this.Services.AddScoped<IInboundErrorInterceptor, InboxErrorInterceptor>();
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
