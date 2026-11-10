using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Outbox;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Middleware;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Fluent builder for configuring durable messaging (outbox, inbox, dead letter queue).
/// The persistence engine is configured separately on the <see cref="Conduit.Configuration.ConduitBuilder"/>
/// (e.g. <c>conduit.UseSqlite(...)</c>) and shared by all features via DI
/// (<c>IConduitConnectionFactory</c> + <c>ISqlDialect</c>).
/// </summary>
/// <remarks>
/// Each feature is added once, however often it is asked for: a second <c>AddOutbox</c>, <c>AddInbox</c> or
/// <c>AddDeadLetterQueue</c> (from this builder or another <c>AddDurability</c> call, say a second module of the same
/// host) registers nothing twice, and its callback configures the options the first call registered, so each call
/// changes only what it sets.
/// </remarks>
public sealed class DurabilityBuilder
{
    internal IServiceCollection Services { get; }

    internal DurabilityBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>
    /// Adds the transactional outbox. Calling it again configures the same outbox (see the remarks on
    /// <see cref="DurabilityBuilder"/>). The delivery follows <see cref="OutboxOptions.CaptureOnly"/> as the last call
    /// that sets it leaves it: <see langword="true"/> removes the relay, a later <see langword="false"/> adds it back.
    /// </summary>
    /// <exception cref="OptionsValidationException">
    /// <see cref="OutboxOptions.PersistedProperties"/> lists an unsupported type or an empty key; an interval, a
    /// wait, a timeout or a retention is out of its range (see <see cref="OutboxOptions"/>'s members);
    /// <see cref="OutboxOptions.BatchSize"/> is not positive or <see cref="OutboxOptions.MaxRetries"/> is below 2.
    /// </exception>
    public DurabilityBuilder AddOutbox(Action<OutboxOptions>? configure = null)
    {
        var options = this.RegisteredOptions<OutboxOptions>();
        configure?.Invoke(options);
        options.Validate();

        this.Services.TryAddScoped<IOutboxStore, DbConnectionOutboxStore>();

        // Retrying a failed entry is a row update, so a capture-only host can hand one back to the relay that owns it.
        this.Services.TryAddScoped<IOutbox, OutboxOperations>();

        // The forwarding registrations name their implementation type, so a second call recognizes them.
        this.Services.TryAddScoped<OutboxCapture>();
        this.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboundInterceptor, OutboxCapture>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>()));

        // Writing (unlike delivery) happens in capture-only mode too: rows go on the caller's transaction.
        this.Services.TryAddScoped<IOutboxPendingWriter, OutboxPendingWriter>();
        // Replaced, not tried: the flusher and the relay follow the options as this call leaves them, so a call that
        // sets CaptureOnly undoes the delivery an earlier call registered.
        if (options.CaptureOnly)
        {
            // Another process's relay owns delivery (OutboxOptions.CaptureOnly's remarks).
            this.Services.Replace(ServiceDescriptor.Singleton<IOutboxFlusher, CaptureOnlyOutboxFlusher>());
            this.RemoveHostedService<OutboxRelay>();
        }
        else
        {
            this.Services.Replace(ServiceDescriptor.Scoped<IOutboxFlusher, OutboxCapture>(
                serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>()));
            this.Services.AddHostedService<OutboxRelay>();
        }

        this.Services.TryAddTransient(typeof(OutboxBehavior<,>));
        return this;
    }

    /// <summary>
    /// Adds the inbox (idempotent message processing). A dispatch that fails releases its record, so the broker's
    /// redelivery, a dead-letter replay, or a retry through a trusting HTTP or SignalR edge runs instead of being
    /// discarded as a duplicate. Calling it
    /// again configures the same inbox (see the remarks on <see cref="DurabilityBuilder"/>): one gate, whose record
    /// a second gate in the same dispatch would take for a duplicate.
    /// </summary>
    /// <exception cref="OptionsValidationException">
    /// <see cref="InboxOptions.RetentionPeriod"/> is not positive or is above 3650 days, or
    /// <see cref="InboxOptions.CleanupInterval"/> is not positive or is above one day.
    /// </exception>
    public DurabilityBuilder AddInbox(Action<InboxOptions>? configure = null)
    {
        var options = this.RegisteredOptions<InboxOptions>();
        configure?.Invoke(options);
        options.Validate();

        this.Services.TryAddScoped<IInboxStore, DbConnectionInboxStore>();

        // One gate per dispatch scope, shared by the error interceptor: it releases the record that gate made.
        this.Services.TryAddScoped<InboxInterceptor>();
        this.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboundInterceptor, InboxInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<InboxInterceptor>()));
        // A failed dispatch releases that record: a broker's through the error interceptors, a trusting HTTP or SignalR
        // edge's through the failure observers.
        this.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboundErrorInterceptor, InboxErrorInterceptor>());
        this.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboundFailureObserver, InboxErrorInterceptor>());
        this.Services.AddHostedService<InboxCleanup>();

        return this;
    }

    /// <summary>
    /// Adds the dead letter queue (capture failed messages, browse, replay). Calling it again configures the same
    /// queue (see the remarks on <see cref="DurabilityBuilder"/>).
    /// </summary>
    public DurabilityBuilder AddDeadLetterQueue(Action<DeadLetterQueueOptions>? configure = null)
    {
        var options = this.RegisteredOptions<DeadLetterQueueOptions>();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IDeadLetterStore, DbDeadLetterStore>();
        this.Services.TryAddScoped<IDeadLetters, Runtime.DeadLetterQueue.DeadLetters>();
        this.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInboundErrorInterceptor, DeadLetterErrorInterceptor>());

        return this;
    }

    // The options instance an earlier call registered, so a later call's callback adds to it; registered here on the
    // first call.
    private TOptions RegisteredOptions<TOptions>()
        where TOptions : class, new()
    {
        var registered = this.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IOptions<TOptions>) && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IOptions<TOptions>>()
            .FirstOrDefault();
        if (registered is not null)
        {
            return registered.Value;
        }

        var options = new TOptions();
        this.Services.AddSingleton(Options.Create(options));
        return options;
    }

    private void RemoveHostedService<THostedService>()
        where THostedService : IHostedService
    {
        var descriptor = this.Services.FirstOrDefault(candidate =>
            candidate.ServiceType == typeof(IHostedService)
            && !candidate.IsKeyedService
            && candidate.ImplementationType == typeof(THostedService));
        if (descriptor is not null)
        {
            this.Services.Remove(descriptor);
        }
    }
}
