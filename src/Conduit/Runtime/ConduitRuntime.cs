using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Conduit.Attributes;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Runtime helpers used by the source-generated mediator. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class ConduitRuntime
{
    /// <summary>
    /// Shared JSON serializer options used by the generated mediator for wire serialization.
    /// Uses camelCase naming and string enum conversion to match TypeGen output. Enums are read by NAME only:
    /// an integer ordinal is refused, in range or not, because TypeGen emits enums as string unions and an
    /// ordinal is how an out-of-range value used to reach a foreign-key column as a 500. A closed set
    /// (<see cref="Conduit.Messages.IClosedSet{TSelf}"/>) is read and written as its bare value
    /// (<see cref="ClosedSetJsonConverterFactory"/>), which TypeGen emits as a union of those values.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
            new ClosedSetJsonConverterFactory(),
        },
    };

    /// <summary>
    /// <see cref="JsonOptions"/> for the audit payload only: a member marked <see cref="DoNotAuditAttribute"/> (on the
    /// property, or on the positional record parameter that binds it) is left out, and a member marked <see cref="AuditRedactedAttribute"/> is written as its
    /// <see cref="AuditRedactedAttribute.Placeholder"/>. The generated mediator serializes the
    /// captured request with these; wire serialization keeps <see cref="JsonOptions"/>.
    /// </summary>
    public static JsonSerializerOptions AuditJsonOptions { get; } = new(JsonOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ApplyAuditMemberMarks } },
    };

    private static void ApplyAuditMemberMarks(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        // System.Text.Json binds constructor parameters to properties case-insensitively, and a derived record's base
        // positional parameters bind through the base constructor, so collect from every type up the chain.
        var droppedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var redactedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var type = typeInfo.Type; type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var constructor in type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.Name is null)
                    {
                        continue;
                    }

                    if (parameter.IsDefined(typeof(DoNotAuditAttribute), inherit: false))
                    {
                        droppedParameters.Add(parameter.Name);
                    }

                    if (parameter.IsDefined(typeof(AuditRedactedAttribute), inherit: false))
                    {
                        redactedParameters.Add(parameter.Name);
                    }
                }
            }
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            // Attribute.IsDefined walks an overridden property's base declarations; MemberInfo.IsDefined ignores
            // `inherit` for properties, so a mark on a base virtual property would be lost on its override.
            if (typeInfo.Properties[i].AttributeProvider is not System.Reflection.MemberInfo member)
            {
                continue;
            }

            var dropped = Attribute.IsDefined(member, typeof(DoNotAuditAttribute), inherit: true) || droppedParameters.Contains(member.Name);
            var redacted = Attribute.IsDefined(member, typeof(AuditRedactedAttribute), inherit: true) || redactedParameters.Contains(member.Name);
            if (dropped && redacted)
            {
                throw new InvalidOperationException(
                    $"{typeInfo.Type.FullName}.{member.Name} carries both [DoNotAudit] and [AuditRedacted]; an audited member takes one.");
            }

            if (dropped)
            {
                typeInfo.Properties.RemoveAt(i);
            }
            else if (redacted)
            {
                typeInfo.Properties[i] = Redacted(typeInfo, typeInfo.Properties[i]);
            }
        }
    }

    // A string-typed stand-in under the same JSON name: the placeholder when the member holds a value, null and "" as
    // themselves. Serialize-only -- AuditJsonOptions never reads.
    private static JsonPropertyInfo Redacted(JsonTypeInfo typeInfo, JsonPropertyInfo original)
    {
        var read = original.Get;
        var standIn = typeInfo.CreateJsonPropertyInfo(typeof(string), original.Name);
        standIn.Get = read is null
            ? null
            : target => read(target) switch
            {
                null => null,
                string { Length: 0 } => string.Empty,
                _ => AuditRedactedAttribute.Placeholder,
            };
        return standIn;
    }

    /// <summary>
    /// Reads a wire payload into its message type. A payload the contract cannot read -- an integer enum ordinal,
    /// a wrong-typed field, malformed JSON -- is the caller's fault, so it is a <see cref="BadRequestException"/>
    /// (400) naming the JSON path, never the InternalError a raw <see cref="JsonException"/> becomes. Called by the
    /// generated mediator's wire dispatch; only the READ is mapped, so a handler's own serializer failure stays a 500.
    /// </summary>
    /// <remarks>
    /// Event wire paths (<c>DispatchEventAsync</c>, <c>PublishLocalEnvelopeAsync</c>) still read the payload raw, so an
    /// unreadable event payload is a logged <see cref="JsonException"/>, not a 400.
    /// </remarks>
    public static T DeserializeWire<T>(JsonElement payload)
    {
        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(payload, WireReadOptions);
        }
        catch (JsonException ex)
        {
            // A missing constructor parameter is a stale sender, not a malformed request. Name the members from the
            // contract (not from the exception text), at any depth, and only when the lenient read succeeds -- so the
            // missing member is the SOLE problem; any other fault stays a plain 400 naming its path.
            var (missing, total) = MissingConstructorMembers(typeof(T), payload);
            if (total > 0 && ReadsLeniently<T>(payload))
            {
                throw new ContractMismatchException(missing, total);
            }

            throw new BadRequestException(string.IsNullOrEmpty(ex.Path)
                ? "The request could not be read."
                : $"The request could not be read at '{ex.Path}'.");
        }

        // A JSON null deserializes to null; the handler would NRE on it and answer InternalError. It is the caller's fault.
        return result ?? throw new BadRequestException("The request could not be read.");
    }

    /// <summary>
    /// <see cref="JsonOptions"/> plus <c>RespectRequiredConstructorParameters</c>, used ONLY by
    /// <see cref="DeserializeWire{T}"/>. The shared <see cref="JsonOptions"/> must stay lenient: it also reads stored
    /// <c>Conduit.Jobs</c> payloads, remote replies and events, where a record written before a member was added must
    /// still deserialize.
    /// </summary>
    private static JsonSerializerOptions WireReadOptions { get; } =
        new(JsonOptions) { RespectRequiredConstructorParameters = true };

    private static bool ReadsLeniently<T>(JsonElement payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) is not null;
        }
#pragma warning disable CA1031 // any failure of the lenient read (a constructor or init that throws on the defaults) means "not solely a missing member": the caller answers BadRequest
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>The most members a <see cref="ContractMismatchException"/> names; the rest are counted ("and N more").</summary>
    internal const int MaxNamedMissingMembers = 20;

    /// <summary>
    /// The wire paths of the constructor parameters (of the constructor System.Text.Json binds) that have no C# default
    /// and are absent from <paramref name="payload"/>, at ANY depth: a positional model nested in a property, inside a
    /// list or as a dictionary value is walked too, so a missing member there is named by its path
    /// (<c>columns[0].wipLimit</c>), using the naming policy. Computed from the contract and the payload, never parsed
    /// from the serializer's message. Returns the first <see cref="MaxNamedMissingMembers"/> distinct paths and the
    /// total count; (empty, 0) when nothing is missing or the contract cannot be named safely (the caller then answers a
    /// plain BadRequest).
    /// </summary>
    /// <remarks>
    /// Only constructor parameters STJ binds are checked. A settable or init property of a non-positional model is not,
    /// and neither is a STRUCT without <see cref="JsonConstructorAttribute"/>: STJ builds a struct through its default
    /// constructor and setters, so an absent member silently keeps its default. A dictionary value is named under the
    /// contract path <c>name[*]</c>, never by its client-supplied key.
    /// </remarks>
    private static (IReadOnlyList<string> Named, int Total) MissingConstructorMembers(Type type, JsonElement payload)
    {
        var missing = new MissingSet();
        try
        {
            CollectMissing(type, payload, string.Empty, missing);
        }
#pragma warning disable CA1031 // naming is best effort: an unexpected contract shape must not turn a 400 into a 500
        catch (Exception)
#pragma warning restore CA1031
        {
            return ([], 0);
        }

        return (missing.Named, missing.Total);
    }

    /// <summary>Distinct missing paths: the first <see cref="MaxNamedMissingMembers"/> kept, every one counted.</summary>
    private sealed class MissingSet
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public List<string> Named { get; } = [];

        public int Total => this.seen.Count;

        public void Add(string path)
        {
            if (this.seen.Add(path) && this.Named.Count < MaxNamedMissingMembers)
            {
                this.Named.Add(path);
            }
        }
    }

    private static void CollectMissing(Type type, JsonElement value, string path, MissingSet missing)
    {
        var shape = ShapeOf(type);
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (shape.ElementType is { } elementType)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    CollectMissing(elementType, item, $"{path}[{index}]", missing);
                    index++;
                }
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (shape.DictionaryValueType is { } valueType)
        {
            // The key is the client's text: name the contract path, never the key itself.
            foreach (var entry in value.EnumerateObject())
            {
                CollectMissing(valueType, entry.Value, $"{path}[*]", missing);
            }

            return;
        }

        if (shape.Unnamed)
        {
            return;
        }

        if (shape.Required.Count > 0)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                present.Add(property.Name);
            }

            foreach (var wireName in shape.Required)
            {
                if (!present.Contains(wireName))
                {
                    missing.Add(Join(path, wireName));
                }
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (shape.Members.TryGetValue(property.Name, out var member))
            {
                CollectMissing(member.Type, property.Value, Join(path, member.WireName), missing);
            }
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>What the walk needs of a type, reflected once per type.</summary>
    private sealed record WireShape(
        bool Unnamed,
        Type? ElementType,
        Type? DictionaryValueType,
        IReadOnlyDictionary<string, (string WireName, Type Type)> Members,
        IReadOnlyList<string> Required);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, WireShape> WireShapes = new();

    private static WireShape ShapeOf(Type type) => WireShapes.GetOrAdd(Nullable.GetUnderlyingType(type) ?? type, BuildShape);

    private static WireShape BuildShape(Type type)
    {
        var members = new Dictionary<string, (string WireName, Type Type)>(StringComparer.OrdinalIgnoreCase);
        var byClrName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0 || p.IsDefined(typeof(JsonIgnoreAttribute), true))
            {
                continue;
            }

            var wireName = p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), true)
                .OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name
                ?? JsonOptions.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name;
            members.TryAdd(wireName, (wireName, p.PropertyType)); // case-only twins: the first wins
            byClrName.TryAdd(p.Name, wireName);
        }

        // A polymorphic or untyped member is read by a discriminator or not at all: its declared type does not say
        // which constructor binds, so it is not named (any fault there stays a plain BadRequest).
        var unnamed = type == typeof(object) || type == typeof(JsonElement) || type.IsAbstract || type.IsInterface
            || type.IsDefined(typeof(JsonPolymorphicAttribute), true) || type.IsDefined(typeof(JsonDerivedTypeAttribute), true);

        var required = new List<string>();
        if (!unnamed && BoundConstructor(type) is { } ctor)
        {
            foreach (var parameter in ctor.GetParameters())
            {
                if (parameter.HasDefaultValue || parameter.Name is null)
                {
                    continue;
                }

                required.Add(byClrName.TryGetValue(parameter.Name, out var wire)
                    ? wire
                    : JsonOptions.PropertyNamingPolicy?.ConvertName(parameter.Name) ?? parameter.Name);
            }
        }

        return new WireShape(unnamed, ElementType(type), DictionaryValueType(type), members, required);
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static Type? DictionaryValueType(Type type)
    {
        static bool IsDictionary(Type t) => t.IsGenericType
            && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        var dictionary = IsDictionary(type) ? type : type.GetInterfaces().FirstOrDefault(IsDictionary);
        return dictionary?.GetGenericArguments()[1];
    }

    /// <summary>
    /// The constructor System.Text.Json binds, mirroring its rule: one marked <see cref="JsonConstructorAttribute"/>
    /// (any visibility) wins. Otherwise a STRUCT binds none -- STJ builds it through its default constructor and setters,
    /// so nothing is required -- and a class binds its single public parameterized constructor only when it has no public
    /// parameterless one (a record's copy constructor never counts).
    /// </summary>
    private static System.Reflection.ConstructorInfo? BoundConstructor(Type type)
    {
        var marked = type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(c => c.IsDefined(typeof(JsonConstructorAttribute), false));
        if (marked is not null)
        {
            return marked;
        }

        if (type.IsValueType)
        {
            return null;
        }

        var ctors = type.GetConstructors();
        var candidates = ctors.Where(c => c.GetParameters() is not [{ } only] || only.ParameterType != type).ToArray();
        return candidates.Length == 1 && candidates[0].GetParameters().Length > 0 ? candidates[0] : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex SafeReference();

    /// <summary>
    /// Checks a reply <see cref="DispatchResult{T}"/> for errors and returns the data.
    /// Throws <see cref="RemoteDispatchException"/> (an <see cref="InvalidOperationException"/>, carrying the reference) if the result contains an error.
    /// Used by all transports after receiving a reply from a remote endpoint.
    /// </summary>
    public static object? UnwrapReply(DispatchResult<JsonElement?>? result)
    {
        if (result?.Error is not null)
        {
            var reference = result.Error.Reference is null ? string.Empty : $" (reference {result.Error.Reference})";
            throw new RemoteDispatchException(
                $"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}{reference}", result.Error.Reference);
        }

        return result?.Data;
    }

    // Error-level logs
    private static readonly Action<ILogger, string, string, Exception?> LogTranslatorFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(5, "WireExceptionTranslatorFailed"),
            "Wire exception translator '{Translator}' threw while translating a failure dispatching '{Type}'; skipping it (the exception holds the translator's failure, then the original)");

    private static readonly Action<ILogger, string, string, Exception?> LogDispatchError =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1, "DispatchError"),
            "Unhandled exception dispatching '{Type}' (reference {Reference})");

    private static readonly Action<ILogger, string, Exception?> LogPublishError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PublishError"),
            "Unhandled exception publishing event '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogListenerFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "ListenerFailed"),
            "Event listener '{Listener}' threw an exception");

    // Information-level logs
    private static readonly Action<ILogger, string, string, string, Exception?> LogWireExceptionTranslated =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(6, "WireExceptionTranslated"),
            "Wire exception translator '{Translator}' answered '{Code}' for an unexpected exception dispatching '{Type}'");

    // Debug-level logs
    private static readonly Action<ILogger, string, Exception?> LogDispatchingLocally =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(10, "DispatchingLocally"),
            "Dispatching '{Type}' locally");

    private static readonly Action<ILogger, string, string, Exception?> LogRoutingToEndpoint =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(11, "RoutingToEndpoint"),
            "Routing '{Type}' to endpoint '{Endpoint}'");

    private static readonly Action<ILogger, string, int, Exception?> LogBroadcastingEvent =
        LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(12, "BroadcastingEvent"),
            "Broadcasting '{Type}' to {Count} transport(s)");

    private static readonly Action<ILogger, string, Exception?> LogWireRequestReceived =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(13, "WireRequestReceived"),
            "Wire request received: '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogWireEventReceived =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(14, "WireEventReceived"),
            "Wire event received: '{Type}'");

    private static readonly Action<ILogger, string, string, Exception?> LogForwardingWireRequest =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(15, "ForwardingWireRequest"),
            "Forwarding wire request '{Type}' to endpoint '{Endpoint}'");

    private static readonly Action<ILogger, string, int, Exception?> LogExecutingPipelines =
        LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(16, "ExecutingPipelines"),
            "Executing {Count} pipeline behavior(s) for '{Type}'");
    /// <summary>
    /// Resolves the first route for the given message type. Returns the matched endpoint,
    /// or null for local dispatch. Used for commands and queries only — events broadcast
    /// to all DI-registered transports instead of routing.
    /// Throws if the matched endpoint is missing or has no transport configured.
    /// </summary>
    public static ConduitEndpoint? ResolveRoute(ConduitOptions options, string messageFullTypeName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(messageFullTypeName);

        foreach (var route in options.Routes)
        {
            if (messageFullTypeName.StartsWith(route.Pattern, StringComparison.Ordinal))
            {
                if (!options.Endpoints.TryGetValue(route.EndpointName, out var endpoint))
                {
                    throw new InvalidOperationException(
                        $"Route pattern '{route.Pattern}' matched message '{messageFullTypeName}' " +
                        $"but endpoint '{route.EndpointName}' is not registered. " +
                        $"Ensure AddEndpoint(\"{route.EndpointName}\", ...) is called in your Conduit configuration.");
                }

                return endpoint;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the transport for a named endpoint from DI using keyed services.
    /// Throws a descriptive error if no transport is registered for the endpoint.
    /// </summary>
    public static IConduitTransport ResolveTransport(IServiceProvider serviceProvider, ConduitEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(endpoint);

        return serviceProvider.GetKeyedService<IConduitTransport>(endpoint.Name)
            ?? throw new InvalidOperationException(
                $"Endpoint '{endpoint.Name}' has no transport configured. " +
                $"Call UseHttp(), UseSignalR(), UseAzureServiceBus(), or UseRabbitMQ() in the endpoint configuration.");
    }

    /// <summary>
    /// Resolves pipeline behaviors in the order declared via
    /// <see cref="ConduitBuilder.AddPipeline"/>. Constructs closed generics
    /// from the ordered type list and resolves each from DI.
    /// Used for commands, queries, and events uniformly.
    /// </summary>
    public static IPipelineBehavior<TRequest, TResult>[] ResolvePipelines<TRequest, TResult>(
        ConduitOptions options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (options.PipelineTypes.Count == 0)
        {
            return [];
        }

        var behaviors = new IPipelineBehavior<TRequest, TResult>[options.PipelineTypes.Count];
        for (var i = 0; i < options.PipelineTypes.Count; i++)
        {
            var closedType = options.PipelineTypes[i]
                .MakeGenericType(typeof(TRequest), typeof(TResult));
            behaviors[i] = (IPipelineBehavior<TRequest, TResult>)serviceProvider.GetRequiredService(closedType);
        }

        return behaviors;
    }

    /// <summary>
    /// Chains pipeline behaviors around a handler call. Behaviors execute in
    /// registration order (first registered = outermost wrapper).
    /// </summary>
    public static async Task<TResult> ExecuteWithBehaviors<TRequest, TResult>(
        TRequest request,
        IPipelineBehavior<TRequest, TResult>[] behaviors,
        Func<Task<TResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(handler);

        var next = handler;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.HandleAsync(request, currentNext, cancellationToken);
        }

        return await next();
    }

    /// <summary>
    /// Runs inbound interceptor gates, then deserializes and dispatches a wire request.
    /// Returns DispatchResult with Ok/Fail wrapping.
    /// </summary>
    public static Task<DispatchResult<object>> HandleWireRequest(
        string messageType,
        IEnumerable<IInboundInterceptor> interceptors,
        Func<DispatchEnvelope> createEnvelope,
        Func<string, JsonElement, CancellationToken, Task<object?>> dispatch,
        ILogger logger,
        CancellationToken cancellationToken)
        => HandleWireRequest(messageType, interceptors, createEnvelope, dispatch, logger, translators: null, cancellationToken);

    /// <summary>
    /// As the overload without translators, but an exception no <see cref="ConduitException"/> arm claims is offered to
    /// each <see cref="IWireExceptionTranslator"/> before it becomes <c>InternalError</c>.
    /// </summary>
    public static async Task<DispatchResult<object>> HandleWireRequest(
        string messageType,
        IEnumerable<IInboundInterceptor> interceptors,
        Func<DispatchEnvelope> createEnvelope,
        Func<string, JsonElement, CancellationToken, Task<object?>> dispatch,
        ILogger logger,
        IEnumerable<IWireExceptionTranslator>? translators,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(createEnvelope);
        ArgumentNullException.ThrowIfNull(dispatch);
        LogWireRequestReceived(logger, messageType, null);

        using var activity = ConduitTelemetry.StartActivity(nameof(ConduitRuntime), messageType, "command", nameof(HandleWireRequest));

        var previousContext = MessageContext.Current;

        try
        {
            foreach (var interceptor in interceptors)
            {
                if (!await interceptor.ShouldProcessAsync(messageType, cancellationToken))
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return DispatchResult<object>.Ok(null);
                }
            }

            // Transports establish the wire correlation context via StartDispatchFromTransport
            // before calling in. Only mint a fresh context when no caller pre-established one,
            // otherwise the transport-supplied MessageId/CausationId would be discarded.
            if (previousContext is null)
            {
                MessageContext.StartDispatch();
            }

            var envelope = createEnvelope();
            var result = await dispatch(envelope.Type, envelope.Payload, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return DispatchResult<object>.Ok(result);
        }
        catch (ConduitException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // Operands, not prose: a refusal that named its subject in the message alone forced every
            // client to render server text verbatim. The Fail overload has always taken details
            // (DispatchResult.cs:34) -- the exception simply had nothing to hand it until now. This
            // overload also forwards the declared HTTP status and, when the exception is client-typed
            // (`ConduitException.IsClientTyped`), its type name and data (ConduitException.GetClientData).
            return DispatchResult<object>.Fail(ex);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            foreach (var translator in translators ?? [])
            {
                ConduitException? refusal = null;
#pragma warning disable CA1031
                try
                {
                    refusal = translator.Translate(ex);
                }
                catch (Exception translatorFailure)
#pragma warning restore CA1031
                {
                    // A faulty translator must not replace the real failure: log it beside the original and fall through.
                    LogTranslatorFailed(logger, translator.GetType().Name, messageType, new AggregateException(translatorFailure, ex));
                }

                if (refusal is not null)
                {
                    // A translator is a backstop (an exception that escaped the pipeline), so its use is logged, not silent.
                    LogWireExceptionTranslated(logger, translator.GetType().Name, refusal.Code.Value, messageType, ex);
                    activity?.SetStatus(ActivityStatusCode.Error, refusal.Message);
                    return DispatchResult<object>.Fail(refusal);
                }
            }

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // A reference the user can quote and support can grep: the dispatch's ConversationId. ConduitHub mints a
            // fresh one per browser invocation, a batch's items share their batch's, and it is written INTO the log line,
            // so finding it needs no log scopes. Refusals (the ConduitException arm above) carry none -- they are not bugs.
            // But the hub accepts a caller-supplied context, so the ConversationId is attacker-controlled: CR/LF would
            // forge log lines. Use it only when it is log-safe ([A-Za-z0-9-]{1,64}); otherwise mint one.
            var conversationId = MessageContext.Current?.ConversationId;
            var reference = conversationId is not null && SafeReference().IsMatch(conversationId)
                ? conversationId
                : Guid.NewGuid().ToString("N");
            LogDispatchError(logger, messageType, reference, ex);
            var failed = DispatchResult<object>.Fail("InternalError", "An unexpected error occurred.");
            return failed with { Error = failed.Error! with { Reference = reference } };
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Runs inbound interceptor gates, then deserializes and dispatches a wire event.
    /// Lets <see cref="EventListenerException"/> propagate to the transport for retry/dead-letter.
    /// Swallows all other exceptions.
    /// </summary>
    public static async Task HandleWireEvent(
        string messageType,
        IEnumerable<IInboundInterceptor> interceptors,
        Func<DispatchEnvelope> createEnvelope,
        Func<string, JsonElement, CancellationToken, Task> dispatch,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(createEnvelope);
        ArgumentNullException.ThrowIfNull(dispatch);
        LogWireEventReceived(logger, messageType, null);

        using var activity = ConduitTelemetry.StartActivity(nameof(ConduitRuntime), messageType, "event", nameof(HandleWireEvent));

        var previousContext = MessageContext.Current;

        try
        {
            foreach (var interceptor in interceptors)
            {
                if (!await interceptor.ShouldProcessAsync(messageType, cancellationToken))
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return;
                }
            }

            // Transports establish the wire correlation context via StartDispatchFromTransport
            // before calling in. Only mint a fresh context when no caller pre-established one,
            // otherwise the transport-supplied MessageId/CausationId would be discarded.
            if (previousContext is null)
            {
                MessageContext.StartDispatch();
            }

            var envelope = createEnvelope();
            await dispatch(envelope.Type, envelope.Payload, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (EventListenerException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogPublishError(logger, messageType, ex);
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Wraps a void Task as Task&lt;object&gt; for pipeline behavior compatibility.
    /// Returns <see cref="Unit.Value"/> so the result is never null and never boxes.
    /// </summary>
    public static async Task<object> VoidToObject(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await action();
        return Unit.Value;
    }

    /// <summary>
    /// Casts Task&lt;TSource&gt; to Task&lt;TTarget&gt; without allocating.
    /// Used by generic interface fallback methods.
    /// </summary>
    public static Task<TTarget> CastTask<TSource, TTarget>(Task<TSource> task)
    {
        return Unsafe.As<Task<TSource>, Task<TTarget>>(ref task);
    }

    /// <summary>
    /// Registers core Conduit infrastructure (options). Called by generated code.
    /// </summary>
    public static IServiceCollection AddConduitCore(
        IServiceCollection services,
        Action<ConduitBuilder>? configure = null)
    {
        var builder = new ConduitBuilder(services);
        configure?.Invoke(builder);

        var options = builder.Options;

        // Register as factory so ApplicationName can default from IHostEnvironment at resolution time
        ServiceCollectionServiceExtensions.AddSingleton(services, serviceProvider =>
        {
            if (string.IsNullOrWhiteSpace(options.ApplicationName))
            {
                var hostEnvironment = serviceProvider
                    .GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();

                if (!string.IsNullOrWhiteSpace(hostEnvironment?.ApplicationName))
                {
                    options.ApplicationName = hostEnvironment.ApplicationName;
                }
            }

            return options;
        });

        // Background dispatcher for [FireAndForget] commands and events
        services.AddSingleton<ConduitBackgroundDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ConduitBackgroundDispatcher>());

        // Transport dispatcher for outbox and other infrastructure that sends envelopes
        services.AddSingleton<TransportDispatcher>();

        return services;
    }

    // Logging helpers called by generated mediator code.
    // These are thin wrappers around the LoggerMessage delegates above.

    /// <summary>Logs that a message is being dispatched locally.</summary>
    public static void LogLocalDispatch(ILogger logger, string typeName)
        => LogDispatchingLocally(logger, typeName, null);

    /// <summary>Logs that a message is being routed to a remote endpoint.</summary>
    public static void LogRouteDispatch(ILogger logger, string typeName, string endpointName)
        => LogRoutingToEndpoint(logger, typeName, endpointName, null);

    /// <summary>Logs that an event is being broadcast to transports.</summary>
    public static void LogBroadcast(ILogger logger, string typeName, int transportCount)
        => LogBroadcastingEvent(logger, typeName, transportCount, null);

    /// <summary>Logs that a wire request is being forwarded to a route.</summary>
    public static void LogWireForward(ILogger logger, string typeName, string endpointName)
        => LogForwardingWireRequest(logger, typeName, endpointName, null);

    /// <summary>Logs pipeline behavior count for a dispatch.</summary>
    public static void LogPipelines(ILogger logger, string typeName, int count)
        => LogExecutingPipelines(logger, typeName, count, null);

    /// <summary>Logs that an individual event listener threw an exception.</summary>
    public static void LogListenerFailure(ILogger logger, string listenerName, Exception exception)
        => LogListenerFailed(logger, listenerName, exception);

}
