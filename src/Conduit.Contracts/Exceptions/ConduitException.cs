using System.Reflection;
using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Exceptions;

/// <summary>
/// Base class for domain exceptions that carry a machine-readable error code
/// for automatic wire-level error translation.
/// </summary>
public abstract class ConduitException : Exception
{
    /// <summary>
    /// Initializes a refusal with its code, its English message and, optionally, its structured operands.
    /// </summary>
    /// <param name="code">The machine-readable error code with HTTP status mapping. Required.</param>
    /// <param name="message">The refusal's English message.</param>
    /// <param name="details">Structured operands for this refusal, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    protected ConduitException(ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail>? details = null)
        : base(message)
    {
        // Every consumer of a refusal reads Code (the wire, Problem Details, logs): a null one would throw a
        // NullReferenceException from inside the runtime's catch (ConduitException) arm, escaping it
        ArgumentNullException.ThrowIfNull(code);
        this.Code = code;
        this.Details = details;
    }

    /// <summary>
    /// The machine-readable error code with HTTP status mapping.
    /// </summary>
    public ErrorCode Code { get; }

    /// <summary>
    /// Structured operands for this refusal, forwarded onto the wire by
    /// <c>ConduitRuntime.HandleWireRequest</c>. <see langword="null"/> -- the default -- means the
    /// refusal carries only its code and message, which is what every pre-existing throw site means.
    /// </summary>
    /// <remarks>
    /// <b>The REST transport does not forward these.</b> <c>ConduitRestHandlers.ToProblem</c> builds a
    /// <c>ProblemDetails</c> from the status and message only, and the global
    /// exception-handler path in a host (DMS's <c>ConduitExceptionHandler</c>) deliberately sends the
    /// status alone. Details reach a client only through the dispatch envelope
    /// (<c>/api/conduit/dispatch</c> and the SignalR hub), which is the path a SPA mediator uses.
    /// </remarks>
    public IReadOnlyList<DispatchErrorDetail>? Details { get; }

    /// <summary>
    /// A support reference to carry onto the wire (<c>DispatchError.Reference</c>) when this exception wraps an
    /// unexpected failure -- e.g. a batch item that failed unexpectedly, so the batch can keep the item's reference.
    /// <see langword="null"/> for an ordinary refusal. Never part of <see cref="GetClientData"/>.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>
    /// The name and values of this refusal's sentence, so a host can write it in the reader's language.
    /// <see langword="null"/> -- the default -- means the refusal has only its English <see cref="Exception.Message"/>.
    /// The English message stays the source of truth inside the server (logs, activity status, audit); this is read
    /// only where the refusal becomes a response. Declared here, on the base class, so it is never part of
    /// <see cref="GetClientData"/> or TypeGen's output.
    /// </summary>
    public RefusalText? Text { get; init; }

    /// <summary>
    /// The typed data a client reads for this refusal: the public instance properties a SUBCLASS declares (on
    /// itself or on an ancestor below <see cref="ConduitException"/>), keyed by property name. An EMPTY dictionary
    /// when <see cref="IsClientTyped"/> is true for this type but it declares no properties — a typed refusal's
    /// data is an empty object, never null. <see langword="null"/> when this type is not client-typed at all —
    /// every plain built-in status exception. Override to shape it yourself.
    /// </summary>
    /// <remarks>Never includes <see cref="Exception"/>'s or this type's own members: a refusal's data is what
    /// its author declared, never a stack trace.</remarks>
    public virtual object? GetClientData()
    {
        var properties = ClientDataProperties(this.GetType());
        return properties.Count == 0
            ? (IsClientTyped(this.GetType()) ? new Dictionary<string, object?>(StringComparer.Ordinal) : null)
            : properties.ToDictionary(
                p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.GetValue(this), StringComparer.Ordinal);
    }

    /// <summary>The type name a client narrows on, or <see langword="null"/> when <see cref="IsClientTyped"/> is
    /// false for this type.</summary>
    public virtual string? GetClientType() => IsClientTyped(this.GetType()) ? this.GetType().FullName : null;

    /// <summary>
    /// Whether <paramref name="type"/> reaches a client as a TYPED refusal — named on the wire and in TypeGen's
    /// <c>ErrorMap</c>, so the client can narrow it with <c>isError</c>. True for any non-abstract subclass declared
    /// OUTSIDE the Conduit.Contracts assembly (the one this type itself lives in) — a consumer's refusal, with or
    /// without data — and for any subclass that declares data. A subclass declared in another Conduit assembly
    /// (e.g. <c>src/Conduit</c>) would be typed too, by the same rule; none exists today — every built-in status
    /// exception lives in Conduit.Contracts alongside <see cref="ConduitException"/>. Conduit's built-in status
    /// exceptions are untyped: a plain <c>ConflictException</c> is a category, not a refusal a screen acts on.
    /// TypeGen re-states this rule by name (<c>ErrorTypeScanner</c>) because it reflects under a metadata context
    /// and cannot call it.
    /// </summary>
    public static bool IsClientTyped(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        // No separate `type == typeof(ConduitException)` check: ConduitException is abstract, so
        // `type.IsAbstract` already excludes it.
        if (type.IsAbstract || !typeof(ConduitException).IsAssignableFrom(type))
        {
            return false;
        }

        return type.Assembly != typeof(ConduitException).Assembly || ClientDataProperties(type).Count > 0;
    }

    /// <summary>
    /// The properties <see cref="GetClientData"/> serializes and TypeGen emits — ONE rule for both, so the wire and
    /// the generated interface cannot disagree.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> ClientDataProperties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var result = new List<PropertyInfo>();
        for (var t = type; t is not null && t != typeof(ConduitException); t = t.BaseType)
        {
            result.AddRange(t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetIndexParameters().Length == 0));
        }

        return result;
    }
}
