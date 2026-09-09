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
}
