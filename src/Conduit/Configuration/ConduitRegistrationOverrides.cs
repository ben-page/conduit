using Conduit.Handlers;
using Conduit.Messages;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Configuration;

/// <summary>
/// Fluent builder for overriding which message types a host handles at runtime.
/// Used after <c>AddConduit()</c> to remove auto-registered handlers and filter
/// the type registry so transports only create topology for the specified types.
/// </summary>
/// <remarks>
/// <c>Clear*</c> methods remove handler registrations from DI and set exclusion
/// filters on the <see cref="ConduitTypeRegistry"/>. Without the types in the
/// active registry, broker transports won't create inbound topology (subscriptions,
/// bindings, consumer groups) for those message types.
///
/// <c>Handle*</c> methods register a handler in DI. For commands/queries, the new
/// handler replaces any existing one (DI last-registration-wins). For events, call
/// <c>ClearEventListeners</c> first to remove existing listeners.
/// </remarks>
public sealed class ConduitRegistrationOverrides
{
    private readonly IServiceCollection services;
    private bool commandsCleared;
    private bool queriesCleared;
    private bool eventsCleared;
    private readonly List<Type> retainedCommandTypes = [];
    private readonly List<Type> retainedQueryTypes = [];
    private readonly List<Type> retainedEventTypes = [];

    internal ConduitRegistrationOverrides(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>
    /// Removes all command handler registrations from DI and excludes all command
    /// types from the type registry. Commands explicitly re-added via <c>Handle</c>
    /// after this call are retained.
    /// </summary>
    public ConduitRegistrationOverrides ClearCommandHandlers()
    {
        RemoveServicesByInterface(this.services, typeof(ICommandHandler<,>));
        RemoveServicesByInterface(this.services, typeof(ICommandHandler<>));
        this.commandsCleared = true;
        return this;
    }

    /// <summary>
    /// Removes all query handler registrations from DI and excludes all query
    /// types from the type registry. Queries explicitly re-added via <c>HandleQuery</c>
    /// after this call are retained.
    /// </summary>
    public ConduitRegistrationOverrides ClearQueryHandlers()
    {
        RemoveServicesByInterface(this.services, typeof(IQueryHandler<,>));
        this.queriesCleared = true;
        return this;
    }

    /// <summary>
    /// Removes all event listener registrations from DI and excludes all event
    /// types from the type registry. Events explicitly re-added via <c>HandleEvent</c>
    /// after this call are retained.
    /// </summary>
    public ConduitRegistrationOverrides ClearEventListeners()
    {
        RemoveServicesByInterface(this.services, typeof(IEventListener<>));
        this.eventsCleared = true;
        return this;
    }

    /// <summary>
    /// Removes all handler and listener registrations. Equivalent to calling
    /// <see cref="ClearCommandHandlers"/>, <see cref="ClearQueryHandlers"/>,
    /// and <see cref="ClearEventListeners"/>.
    /// </summary>
    public ConduitRegistrationOverrides ClearAllHandlers()
    {
        this.ClearCommandHandlers();
        this.ClearQueryHandlers();
        this.ClearEventListeners();
        return this;
    }

    /// <summary>
    /// Registers a command handler (with result) and retains its type in the registry.
    /// </summary>
    public ConduitRegistrationOverrides Handle<TCommand, TResult, THandler>()
        where TCommand : class, ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        this.services.AddTransient<ICommandHandler<TCommand, TResult>, THandler>();
        this.retainedCommandTypes.Add(typeof(TCommand));
        return this;
    }

    /// <summary>
    /// Registers a void command handler and retains its type in the registry.
    /// </summary>
    public ConduitRegistrationOverrides Handle<TCommand, THandler>()
        where TCommand : class, ICommand
        where THandler : class, ICommandHandler<TCommand>
    {
        this.services.AddTransient<ICommandHandler<TCommand>, THandler>();
        this.retainedCommandTypes.Add(typeof(TCommand));
        return this;
    }

    /// <summary>
    /// Registers a query handler and retains its type in the registry.
    /// </summary>
    public ConduitRegistrationOverrides HandleQuery<TQuery, TResult, THandler>()
        where TQuery : class, IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        this.services.AddTransient<IQueryHandler<TQuery, TResult>, THandler>();
        this.retainedQueryTypes.Add(typeof(TQuery));
        return this;
    }

    /// <summary>
    /// Registers an event listener and retains its type in the registry.
    /// </summary>
    public ConduitRegistrationOverrides HandleEvent<TEvent, TListener>()
        where TEvent : class, IEvent
        where TListener : class, IEventListener<TEvent>
    {
        this.services.AddTransient<IEventListener<TEvent>, TListener>();
        this.retainedEventTypes.Add(typeof(TEvent));
        return this;
    }

    /// <summary>
    /// Applies exclusion filters to the <see cref="ConduitTypeRegistry"/>.
    /// Called internally after the configure callback completes.
    /// </summary>
    internal void Apply()
    {
        // Find the registry in DI — it's registered as a singleton instance by the source gen
        var registryDescriptor = this.services.FirstOrDefault(
            d => d.ServiceType == typeof(ConduitTypeRegistry));

        if (registryDescriptor?.ImplementationInstance is not ConduitTypeRegistry registry)
        {
            return;
        }

        if (this.commandsCleared)
        {
            if (this.retainedCommandTypes.Count > 0)
            {
                var retainedNames = new HashSet<string>(
                    this.retainedCommandTypes.Select(t => t.FullName!), StringComparer.Ordinal);
                var toExclude = registry.CommandTypes
                    .Where(t => !retainedNames.Contains(t.FullName!)).ToList();
                registry.ExcludeCommandTypes(toExclude);
            }
            else
            {
                registry.ExcludeAllCommandTypes();
            }
        }

        if (this.queriesCleared)
        {
            if (this.retainedQueryTypes.Count > 0)
            {
                var retainedNames = new HashSet<string>(
                    this.retainedQueryTypes.Select(t => t.FullName!), StringComparer.Ordinal);
                var toExclude = registry.QueryTypes
                    .Where(t => !retainedNames.Contains(t.FullName!)).ToList();
                registry.ExcludeQueryTypes(toExclude);
            }
            else
            {
                registry.ExcludeAllQueryTypes();
            }
        }

        if (this.eventsCleared)
        {
            if (this.retainedEventTypes.Count > 0)
            {
                var retainedNames = new HashSet<string>(
                    this.retainedEventTypes.Select(t => t.FullName!), StringComparer.Ordinal);
                var toExclude = registry.EventTypes
                    .Where(t => !retainedNames.Contains(t.FullName!)).ToList();
                registry.ExcludeEventTypes(toExclude);
            }
            else
            {
                registry.ExcludeAllEventTypes();
            }
        }
    }

    private static void RemoveServicesByInterface(IServiceCollection services, Type openGenericInterface)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var serviceType = services[i].ServiceType;
            if (serviceType.IsGenericType &&
                serviceType.GetGenericTypeDefinition() == openGenericInterface)
            {
                services.RemoveAt(i);
            }
        }
    }
}
