using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
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
    /// property, or on the positional record parameter that binds it) is left out, a member marked <see cref="AuditRedactedAttribute"/> is written as its
    /// <see cref="AuditRedactedAttribute.Placeholder"/>, and a raw-JSON member marked <see cref="AuditAsAttribute"/> is written
    /// as the message it holds, read into its type and redacted by that type's own marks (left out when that type records
    /// no body: type-level <see cref="DoNotAuditAttribute"/>, or suppressed by the host's audit registration). The generated mediator serializes the
    /// captured request with these; wire serialization keeps <see cref="JsonOptions"/>.
    /// </summary>
    public static JsonSerializerOptions AuditJsonOptions { get; } = new(JsonOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ApplyAuditMemberMarks } },
    };

    private static readonly JsonElement PlaceholderElement = JsonSerializer.SerializeToElement(AuditRedactedAttribute.Placeholder);

    // Message types whose body the host's audit registration keeps out (Conduit.Audit's Suppress<T>/SuppressPayload<T>).
    // Process-wide, like AuditJsonOptions itself, and add-only: a type suppressed by any host in the process is never
    // written inside an [AuditAs] member, so a second host can only redact more, never less.
    private static readonly ConcurrentDictionary<Type, byte> AuditSuppressedPayloadTypes = new();

    /// <summary>
    /// Records that the host's audit registration keeps <paramref name="type"/>'s body out of the trail
    /// (<c>Suppress&lt;T&gt;()</c> or <c>SuppressPayload&lt;T&gt;()</c>), so an <see cref="AuditAsAttribute"/> member holding
    /// one is left out of <see cref="AuditJsonOptions"/>' output, as it is for a type-level <see cref="DoNotAuditAttribute"/>.
    /// Called by Conduit.Audit's <c>AddAudit</c>. Process-wide and add-only.
    /// </summary>
    internal static void SuppressAuditPayloadOf(Type type) => AuditSuppressedPayloadTypes.TryAdd(type, 0);

    /// <summary>
    /// Whether a dispatch of <paramref name="type"/> captures an audit body: false for a type marked
    /// <see cref="DoNotAuditAttribute"/> at type level or suppressed by the host's audit registration
    /// (<c>Suppress&lt;T&gt;()</c>/<c>SuppressPayload&lt;T&gt;()</c>, process-wide). The generated in-process capture and
    /// <see cref="CaptureAuditPayload{T}"/> check it before serializing, so a body that is never kept is never
    /// serialized -- and a type whose serialization would throw cannot fail its dispatch through capture.
    /// </summary>
    public static bool CapturesAuditPayload(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return !CapturesNoBody(type);
    }

    // Whether a dispatch of this message type records no body: a type-level [DoNotAudit] (the generator's rule) or the
    // host's audit suppression (SuppressAuditPayloadOf).
    private static bool CapturesNoBody(Type type) =>
        type.IsDefined(typeof(DoNotAuditAttribute), inherit: false) || AuditSuppressedPayloadTypes.ContainsKey(type);

    private static void ApplyAuditMemberMarks(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var parameters = ConstructorParameters(typeInfo.Type);
        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].AttributeProvider is not System.Reflection.MemberInfo member)
            {
                continue;
            }

            var dropped = AuditMark<DoNotAuditAttribute>(member, parameters) is not null;
            var redacted = AuditMark<AuditRedactedAttribute>(member, parameters) is not null;
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
            else if (AuditMark<AuditAsAttribute>(member, parameters) is { } auditAs)
            {
                typeInfo.Properties[i] = TypedAtCapture(typeInfo, typeInfo.Properties[i], auditAs.TypeMember);
            }
        }
    }

    // System.Text.Json binds constructor parameters to properties case-insensitively, and a derived record's base
    // positional parameters bind through the base constructor, so collect from every type up the chain.
    private static ILookup<string, System.Reflection.ParameterInfo> ConstructorParameters(Type type)
    {
        var parameters = new List<System.Reflection.ParameterInfo>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var constructor in current.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                parameters.AddRange(constructor.GetParameters().Where(parameter => parameter.Name is not null));
            }
        }

        return parameters.ToLookup(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);
    }

    // The member's mark, or the mark on a constructor parameter that binds it. Attribute.GetCustomAttribute walks an
    // overridden property's base declarations; MemberInfo.GetCustomAttribute ignores `inherit` for properties, so a mark on
    // a base virtual property would be lost on its override.
    private static TMark? AuditMark<TMark>(System.Reflection.MemberInfo member, ILookup<string, System.Reflection.ParameterInfo> parameters)
        where TMark : Attribute =>
        Attribute.GetCustomAttribute(member, typeof(TMark), inherit: true) as TMark
        ?? parameters[member.Name]
            .Select(parameter => Attribute.GetCustomAttribute(parameter, typeof(TMark), inherit: false) as TMark)
            .FirstOrDefault(mark => mark is not null);

    // A string-typed stand-in under the same JSON name: the placeholder when the member holds a value, null and "" as
    // themselves. Serialize-only -- AuditJsonOptions never reads.
    private static JsonPropertyInfo Redacted(JsonTypeInfo typeInfo, JsonPropertyInfo original)
    {
        var read = original.Get;
        var standIn = typeInfo.CreateJsonPropertyInfo(typeof(string), original.Name);
        standIn.Order = original.Order;
        standIn.IsRequired = original.IsRequired;
        // The original predicate (e.g. [JsonIgnore(Condition = WhenWritingNull)]) judges the REAL value, so an absent
        // redacted member is still omitted where the original would have been.
        standIn.ShouldSerialize = original.ShouldSerialize is { } shouldSerialize && read is not null
            ? (target, _) => shouldSerialize(target, read(target))
            : null;
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

    // An [AuditAs] member: a JsonElement-typed stand-in under the same JSON name holding the inner message, typed by the
    // sibling string property's wire name and serialized with AuditJsonOptions -- so its own marks apply, recursively.
    // Never the raw JSON: a misapplied mark (not a JsonElement member, no sibling string property) is redacted outright,
    // and an inner message that fails to read or write is the placeholder -- never thrown, because a throw here would
    // fail the dispatch being captured. Serialize-only.
    private static JsonPropertyInfo TypedAtCapture(JsonTypeInfo typeInfo, JsonPropertyInfo original, string typeMember)
    {
        var read = original.Get;
        var typeName = SiblingProperty(typeInfo.Type, typeMember);
        if (read is null
            || (original.PropertyType != typeof(JsonElement) && original.PropertyType != typeof(JsonElement?))
            || typeName is not { PropertyType: var namePropertyType, CanRead: true }
            || namePropertyType != typeof(string))
        {
            return Redacted(typeInfo, original);
        }

        // The inner message type, or null when the name resolves to no registered message type.
        Type? InnerType(object target) =>
            typeName.GetValue(target) is string name && ConduitTypeRegistry.TryGetRegisteredMessageType(name, out var type)
                ? type
                : null;

        var standIn = typeInfo.CreateJsonPropertyInfo(typeof(JsonElement?), original.Name);
        standIn.Order = original.Order;
        standIn.IsRequired = original.IsRequired;
        // A message type whose dispatch records no body -- type-level [DoNotAudit], or suppressed by the host's audit
        // registration -- is left out, as a [DoNotAudit] member is. The original predicate still judges the REAL value.
        standIn.ShouldSerialize = (target, _) =>
            !(InnerType(target) is { } type && CapturesNoBody(type))
            && (original.ShouldSerialize is not { } shouldSerialize || shouldSerialize(target, read(target)));
        standIn.Get = target =>
        {
            if (read(target) is not JsonElement element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return null;
            }

            if (InnerType(target) is not { } type)
            {
                return PlaceholderElement;
            }

            if (CapturesNoBody(type))
            {
                return null;
            }

            // Any failure -- unreadable JSON, a contract whose constructor or a converter throws, a conflicting mark on the
            // inner type -- is the placeholder. Only an out-of-memory condition propagates.
            try
            {
                return element.Deserialize(type, JsonOptions) is { } message
                    ? JsonSerializer.SerializeToElement(message, type, AuditJsonOptions)
                    : PlaceholderElement;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return PlaceholderElement;
            }
        };
        return standIn;
    }

    // The sibling the [AuditAs] argument names: the most-derived declaration of an instance, non-indexed property, walking
    // the base chain declaration by declaration -- so a `new`-hidden sibling resolves to the derived one instead of the
    // AmbiguousMatchException Type.GetProperty throws (inside the type-info modifier, that would fail every capture).
    private static System.Reflection.PropertyInfo? SiblingProperty(Type type, string name)
    {
        const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current.GetProperties(declared).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0) is { } property)
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a wire payload into its message type. A payload the contract cannot read -- an integer enum ordinal,
    /// a wrong-typed field, malformed JSON -- is the caller's fault, so it is a <see cref="BadRequestException"/>
    /// (400) naming the JSON path, never the InternalError a raw <see cref="JsonException"/> becomes. Called by the
    /// generated mediator's wire dispatch; only the READ is mapped, so a handler's own serializer failure stays a 500.
    /// </summary>
    /// <remarks>
    /// Event wire paths (<c>DispatchEventAsync</c>, <c>PublishLocalEnvelopeAsync</c>) still read the payload raw, so an
    /// unreadable event payload is a logged <see cref="JsonException"/>, not a 400. <c>PublishLocalEnvelopeAsync</c> reads inside an
    /// observed dispatch that keeps no body, so its failed read is a failed delivery record.
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
    /// Records <paramref name="message"/> as the current dispatch's audit payload and returns it unchanged. The
    /// generated wire dispatch wraps each arm's read in it, so a wire request is captured the way an in-process one is:
    /// the TYPED object serialized with <see cref="AuditJsonOptions"/>, never the raw client JSON (which would keep
    /// <see cref="DoNotAuditAttribute"/> members and any property the client invented). A member typed
    /// <see cref="JsonElement"/>, <c>JsonNode</c> or <c>object</c>, or a <c>[JsonExtensionData]</c> bag, is written as
    /// the raw JSON it holds -- <see cref="AuditJsonOptions"/> cannot redact inside it -- unless it is marked
    /// <see cref="AuditAsAttribute"/> (typed at capture), <see cref="DoNotAuditAttribute"/> or <see cref="AuditRedactedAttribute"/>;
    /// otherwise a message carrying one must have its payload suppressed. No-op without a current
    /// context or without an attached observer, and for a type whose dispatch records no body
    /// (<see cref="CapturesAuditPayload"/>: the host's audit suppression), which is therefore never serialized. A type
    /// marked <see cref="DoNotAuditAttribute"/> at type level is never wrapped -- the generator decides that at compile time.
    /// </summary>
    public static T CaptureAuditPayload<T>(T message)
    {
        var context = MessageContext.Current;
        if (context is { HasObserver: true } && message is not null && !CapturesNoBody(message.GetType()))
        {
            context.SetPayload(JsonSerializer.SerializeToElement<T>(message, AuditJsonOptions));
        }

        return message;
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

    // Warning-level logs. A refusal is the caller's mistake, not the server's, so none carries an exception (or a stack).
    private static readonly Action<ILogger, string, string, string, Exception?> LogRefusedEvent =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(7, "RefusedEvent"),
            "Refused inbound event '{Type}' ({Code}): {Reason}");

    private static readonly Action<ILogger, string, Exception?> LogUnknownMessageTypeMessage =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8, "UnknownMessageType"),
            "Refused a wire request for '{Type}', a message type this host neither handles nor routes");

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
            "Executing pipeline behaviors for '{Type}': {Count}");

    // Dispatch retry logs (DispatchRetryRunner). Information: a retry is expected behaviour, and the failure it answers
    // is the host's to log (its own Warning carries the exception), so DispatchRetrying carries none.
    private static readonly Action<ILogger, string, int, int, string, double, Exception?> LogDispatchRetryingMessage =
        LoggerMessage.Define<string, int, int, string, double>(LogLevel.Information, new EventId(17, "DispatchRetrying"),
            "Dispatching '{Type}' failed on attempt {Attempt} of {MaxAttempts} ({Reason}); retrying in {DelayMs:0} ms");

    private static readonly Action<ILogger, string, int, Exception?> LogDispatchRetrySucceededMessage =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(18, "DispatchRetrySucceeded"),
            "Dispatching '{Type}' succeeded on attempt {Attempt}");

    private static readonly Action<ILogger, string, string, Exception?> LogDispatchRetryPolicyFailedMessage =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(19, "DispatchRetryPolicyFailed"),
            "Dispatch retry policy '{Policy}' threw while deciding whether to retry '{Type}'; not retrying (the exception is the policy's)");

    /// <summary>
    /// The retry boundary every generated handler-running method goes through. Runs <paramref name="attempt"/> once,
    /// on <paramref name="services"/> itself, when no <see cref="IDispatchRetryPolicy"/> is registered
    /// (<see cref="ConduitBuilder.AddDispatchRetry{TPolicy}"/> never called), when the dispatch is nested (it comes from
    /// the current attempt's own scope), or when the policy does not cover <typeparamref name="TRequest"/>. Otherwise
    /// it runs each attempt in a fresh scope from the root factory, retrying while the policy gives a reason and
    /// attempts remain, and rethrows the kept attempt's exception. A <see cref="DispatchAttempt.Current"/> from any
    /// other provider does not make a dispatch nested: it is independent and gets attempts of its own.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<TResult> DispatchAttemptsAsync<TRequest, TResult>(
        IServiceProvider services,
        Func<IServiceProvider, Task<TResult>> attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(attempt);

        var runner = services.GetService<DispatchRetryRunner>();
        if (runner is null)
        {
            return attempt(services);
        }

        if (DispatchAttempt.Current is { } current && ReferenceEquals(current.Services, services))
        {
            return attempt(services);
        }

        if (!runner.Policy.Covers(typeof(TRequest)))
        {
            return attempt(services);
        }

        return runner.RunAsync<TRequest, TResult>(services, attempt, cancellationToken);
    }

    /// <summary>
    /// Records on <paramref name="context"/> the attempt that owns its dispatch: <see cref="DispatchAttempt.Current"/>
    /// when the dispatching provider is that attempt's own scope (the nested rule), otherwise null. Called by the
    /// generated mediator right after it starts a dispatch's context. A null context is a no-op.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void BindDispatchAttempt(MessageContext? context, IServiceProvider services)
    {
        if (context is null)
        {
            return;
        }

        context.Attempt = DispatchAttempt.Current is { } current && ReferenceEquals(current.Services, services)
            ? current
            : null;
    }

    /// <summary>
    /// Resolves the first route for the given message type. Returns the matched endpoint,
    /// or null for local dispatch. An exact route matches only its own type; a namespace route
    /// matches its namespace and the namespaces beneath it, on whole segments (see <see cref="ConduitRoute"/>). Used for commands and queries only — events broadcast
    /// to all DI-registered transports instead of routing.
    /// Throws if the matched endpoint is missing or has no transport configured.
    /// </summary>
    public static ConduitEndpoint? ResolveRoute(ConduitOptions options, string messageFullTypeName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(messageFullTypeName);

        foreach (var route in options.Routes)
        {
            if (route.Matches(messageFullTypeName))
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
                $"Call the transport's Use* extension (e.g. UseRabbitMQ) on the endpoint in AddEndpoint(\"{endpoint.Name}\", ...).");
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

        return ResolvePipelines<TRequest, TResult>(options.PipelineTypes, serviceProvider);
    }

    /// <summary>Closes each of <paramref name="pipelineTypes"/>, in order, over the request and result types and resolves it.</summary>
    private static IPipelineBehavior<TRequest, TResult>[] ResolvePipelines<TRequest, TResult>(
        IReadOnlyList<Type> pipelineTypes,
        IServiceProvider serviceProvider)
    {
        if (pipelineTypes.Count == 0)
        {
            return [];
        }

        var behaviors = new IPipelineBehavior<TRequest, TResult>[pipelineTypes.Count];
        for (var i = 0; i < pipelineTypes.Count; i++)
        {
            var closedType = pipelineTypes[i]
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
    /// As the translator overload, but the runtime resolves what it needs from <paramref name="services"/>: today the
    /// <see cref="IWireExceptionTranslator"/>s, and the next runtime service without a generator change. It renders
    /// nothing: a refusal's answer is English and carries <see cref="DispatchError.Text"/> for the edge that writes it
    /// in the reader's language (<see cref="RefusalRendering"/>).
    /// </summary>
    public static Task<DispatchResult<object>> HandleWireRequest(
        string messageType,
        IEnumerable<IInboundInterceptor> interceptors,
        Func<DispatchEnvelope> createEnvelope,
        Func<string, JsonElement, CancellationToken, Task<object?>> dispatch,
        ILogger logger,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return HandleWireRequest(
            messageType, interceptors, createEnvelope, dispatch, logger,
            services.GetServices<IWireExceptionTranslator>(), cancellationToken);
    }

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

        return await RunWireRequestAsync(
            messageType,
            interceptors,
            () =>
            {
                var envelope = createEnvelope();
                return dispatch(envelope.Type, envelope.Payload, cancellationToken);
            },
            logger,
            translators,
            nameof(HandleWireRequest),
            cancellationToken);
    }

    /// <summary>
    /// Runs a wire request's inbound checks -- what <see cref="HandleWireRequest(string, IEnumerable{IInboundInterceptor}, Func{DispatchEnvelope}, Func{string, JsonElement, CancellationToken, Task{object}}, ILogger, IEnumerable{IWireExceptionTranslator}, CancellationToken)"/>
    /// runs ahead of its handler -- and never the handler: the host's <see cref="IInboundInterceptor"/> gates (the
    /// <c>[ClientVisible]</c> type filter), the read (<see cref="DeserializeWire{T}"/>), and every behavior of the host's
    /// pipeline that is an <see cref="IInboundCheckBehavior"/>, in the pipeline's order, closed over the contract's own result
    /// type as dispatch closes them. A refusal or an unexpected failure comes back as the wire answers it, through the same
    /// gate loop and error mapping (the host's <see cref="IWireExceptionTranslator"/>s included); nothing escapes but a
    /// cancellation of <paramref name="cancellationToken"/>, which propagates as it does from the wire.
    /// </summary>
    /// <remarks>
    /// It runs no other behavior (no transaction, no audit), not the contract's own <c>Validate</c>, and attaches no observer,
    /// so it records nothing. A preflight is no transport's message: the gates run under no context, as for a wire request no
    /// transport identified (so an inbox gate records nothing), and the checks in a fresh one; the caller's context is current
    /// again when it returns. A contract this host routes elsewhere is admitted once the gates pass -- the wire forwards it
    /// unread, so its checks are the receiving host's. A name the host's registry does not list as a command or query is
    /// answered as the wire answers an unknown type (<see cref="UnknownMessageType"/>: a <c>BadRequest</c> refusal).
    /// </remarks>
    /// <param name="contractName">The request's wire type name (<see cref="Type.FullName"/>).</param>
    /// <param name="payload">The request as the wire carries it.</param>
    /// <param name="services">The provider the request would dispatch on: its gates, translators, pipeline and checks
    /// resolve from it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<WirePreflightResult> PreflightWireRequestAsync(
        string contractName,
        JsonElement payload,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contractName);
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ConduitRuntime))
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var checksRan = false;
        var callerContext = MessageContext.Current;
        MessageContext.RestoreContext(null);
        try
        {
            var answer = await RunWireRequestAsync(
                contractName,
                services.GetServices<IInboundInterceptor>(),
                async () =>
                {
                    await RunInboundChecksAsync(contractName, payload, services, logger, cancellationToken);
                    checksRan = true;
                    return null;
                },
                logger,
                services.GetServices<IWireExceptionTranslator>(),
                nameof(PreflightWireRequestAsync),
                cancellationToken);
            return new WirePreflightResult(answer.Error is null && checksRan, answer.Error);
        }
        finally
        {
            MessageContext.RestoreContext(callerContext);
        }
    }

    private static readonly System.Reflection.MethodInfo RunInboundChecksOfMethod =
        typeof(ConduitRuntime).GetMethod(nameof(RunInboundChecksOfAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    /// <summary>
    /// The body of a preflight, inside the wire's gate loop and error mapping: as the generated wire dispatch, a routed
    /// contract is not read here and an unknown one is refused (<see cref="UnknownMessageType"/>); otherwise the contract's
    /// checks run over its read payload.
    /// </summary>
    private static Task RunInboundChecksAsync(
        string contractName, JsonElement payload, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var options = services.GetService<ConduitOptions>();
        if (options is not null && ResolveRoute(options, contractName) is not null)
        {
            return Task.CompletedTask;
        }

        var registry = services.GetService<ConduitTypeRegistry>();
        var contract = registry?.CommandTypes.Concat(registry.QueryTypes)
                .FirstOrDefault(type => string.Equals(type.FullName, contractName, StringComparison.Ordinal))
            ?? throw UnknownMessageType(logger, contractName);
        IReadOnlyList<Type> checks = options?.PipelineTypes.Where(typeof(IInboundCheckBehavior).IsAssignableFrom).ToArray() ?? [];

        return RunInboundChecksOfMethod.MakeGenericMethod(contract, ResultTypeOf(contract))
            .CreateDelegate<Func<JsonElement, IReadOnlyList<Type>, IServiceProvider, CancellationToken, Task>>()
            (payload, checks, services, cancellationToken);
    }

    /// <summary>Reads the payload as the wire reads it and chains the checks around a step that does nothing.</summary>
    private static async Task RunInboundChecksOfAsync<TRequest, TResult>(
        JsonElement payload, IReadOnlyList<Type> checks, IServiceProvider services, CancellationToken cancellationToken)
    {
        var request = DeserializeWire<TRequest>(payload);
        await ExecuteWithBehaviors(
            request,
            ResolvePipelines<TRequest, TResult>(checks, services),
            static () => Task.FromResult(default(TResult)!),
            cancellationToken);
    }

    /// <summary>The result type dispatch closes a contract's pipeline over: its <c>ICommand&lt;T&gt;</c> or
    /// <c>IQuery&lt;T&gt;</c> argument, or <see cref="object"/> for a void command.</summary>
    private static Type ResultTypeOf(Type contract) =>
        contract.GetInterfaces()
            .FirstOrDefault(type => type.IsGenericType
                && (type.GetGenericTypeDefinition() == typeof(ICommand<>) || type.GetGenericTypeDefinition() == typeof(IQuery<>)))
            ?.GetGenericArguments()[0]
        ?? typeof(object);

    /// <summary>
    /// The part of a wire request every caller shares: the inbound gates, the dispatch context, <paramref name="body"/>, and
    /// the exception-to-wire mapping. <paramref name="operation"/> names the activity.
    /// </summary>
    private static async Task<DispatchResult<object>> RunWireRequestAsync(
        string messageType,
        IEnumerable<IInboundInterceptor> interceptors,
        Func<Task<object?>> body,
        ILogger logger,
        IEnumerable<IWireExceptionTranslator>? translators,
        string operation,
        CancellationToken cancellationToken)
    {
        // "request", as the dispatch metric tags an undeclared one: the caller names the type, and a name alone does not
        // say whether it is a command or a query.
        using var activity = ConduitTelemetry.StartActivity(nameof(ConduitRuntime), messageType, "request", operation);

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

            var result = await body();
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
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (an HTTP client disconnected, a hub connection closed): there is no one to answer, and
            // it is no server failure -- no Error line, no support reference. A cancellation the caller did not ask for
            // (a handler's own timeout) falls through to the arm below.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
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
            // The ConversationId can come from outside: a trusting HTTP or SignalR edge takes it from its caller (checked
            // there), a broker transport from the message (unchecked: a foreign producer can send anything), and a host
            // can start a context itself. CR/LF would forge log lines, so use it only when it is a plain token
            // (MessageContextIds); otherwise mint one.
            var conversationId = MessageContext.Current?.ConversationId;
            var reference = MessageContextIds.IsValid(conversationId)
                ? conversationId
                : Guid.NewGuid().ToString("N");
            LogDispatchError(logger, messageType, reference, ex);
            var failed = DispatchResult<object>.Fail("InternalError", "An unexpected error occurred.");
            return failed with
            {
                Error = failed.Error! with { Reference = reference, Text = new RefusalText(ConduitTextNames.UnexpectedError) },
            };
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Runs inbound interceptor gates, then deserializes and dispatches a wire event.
    /// Lets <see cref="EventListenerException"/> propagate to the transport for retry/dead-letter, and a refusal
    /// (<see cref="ConduitException"/>: a gate such as the type filter, or an unknown type) to the transport to answer its
    /// caller, logged at Warning without a stack (EventId 7, <c>RefusedEvent</c>). A cancellation of
    /// <paramref name="cancellationToken"/> propagates unlogged. Logs and swallows every other exception.
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
        catch (ConduitException ex)
        {
            // A refusal is answered to the publisher (the HTTP /publish endpoint, the hub's Publish); a broker transport
            // acknowledges it, since a redelivery would be refused again.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogRefusedEvent(logger, messageType, ex.Code.Value, ex.Message, null);
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
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
    /// Casts Task&lt;TSource&gt; to Task&lt;TTarget&gt; without allocating. Used by the generated generic interface
    /// fallbacks, where <typeparamref name="TTarget"/> is <typeparamref name="TSource"/> at run time.
    /// </summary>
    /// <exception cref="InvalidCastException">
    /// <typeparamref name="TTarget"/> is another type: the task is never reinterpreted as a task of a type it is not.
    /// </exception>
    public static Task<TTarget> CastTask<TSource, TTarget>(Task<TSource> task)
    {
        return (Task<TTarget>)(object)task;
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

    /// <summary>
    /// The refusal for a wire request whose type this host neither handles nor routes, logged at Warning without a stack
    /// (EventId 8, <c>UnknownMessageType</c>). The name is the caller's choice, so it is the caller's mistake: a
    /// <c>BadRequest</c> refusal, never an <c>InternalError</c> with a support reference. The generated wire switch's
    /// default arm throws it, and so does <see cref="PreflightWireRequestAsync"/>.
    /// </summary>
    /// <param name="logger">The logger the refusal is written to.</param>
    /// <param name="type">The wire type name the caller sent.</param>
    /// <returns>The exception for the caller to throw.</returns>
    public static BadRequestException UnknownMessageType(ILogger logger, string type)
    {
        LogUnknownMessageTypeMessage(logger, type, null);
        return new BadRequestException($"Unknown message type '{type}'.");
    }

    internal static void LogDispatchRetrying(
        ILogger logger, string typeName, int attempt, int maxAttempts, string reason, double delayMs)
        => LogDispatchRetryingMessage(logger, typeName, attempt, maxAttempts, reason, delayMs, null);

    internal static void LogDispatchRetrySucceeded(ILogger logger, string typeName, int attempt)
        => LogDispatchRetrySucceededMessage(logger, typeName, attempt, null);

    internal static void LogDispatchRetryPolicyFailed(
        ILogger logger, string policyName, string typeName, Exception exception)
        => LogDispatchRetryPolicyFailedMessage(logger, policyName, typeName, exception);

}
