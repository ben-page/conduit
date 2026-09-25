using System.Reflection;
using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Exceptions;

/// <summary>
/// Base class for domain exceptions that carry a machine-readable error code
/// for automatic wire-level error translation.
/// </summary>
public abstract class ConduitException(
    ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail>? details = null)
    : Exception(message)
{
    /// <summary>
    /// The machine-readable error code with HTTP status mapping.
    /// </summary>
    public ErrorCode Code => code;

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
    public IReadOnlyList<DispatchErrorDetail>? Details => details;

    /// <summary>
    /// The typed data a client reads for this refusal: by default, the public instance properties a SUBCLASS
    /// declares (on itself or on an ancestor below <see cref="ConduitException"/>), keyed by property name.
    /// <see langword="null"/> when there are none — every plain status exception. Override to shape it yourself.
    /// </summary>
    /// <remarks>Never includes <see cref="Exception"/>'s or this type's own members: a refusal's data is what
    /// its author declared, never a stack trace.</remarks>
    public virtual object? GetClientData()
    {
        var properties = ClientDataProperties(this.GetType());
        return properties.Count == 0
            ? null
            : properties.ToDictionary(
                p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.GetValue(this), StringComparer.Ordinal);
    }

    /// <summary>The type name a client narrows on, or <see langword="null"/> when <see cref="GetClientData"/> is.</summary>
    public virtual string? GetClientType() =>
        ClientDataProperties(this.GetType()).Count == 0 ? null : this.GetType().FullName;

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
