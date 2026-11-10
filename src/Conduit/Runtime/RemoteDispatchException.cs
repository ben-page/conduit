using Conduit.Exceptions;

namespace Conduit.Runtime;

/// <summary>
/// A remote endpoint answered a dispatch with an error. Thrown by <see cref="ConduitRuntime.UnwrapReply"/>; still an
/// <see cref="InvalidOperationException"/> so existing catches keep working. <see cref="Code"/> tells a remote refusal
/// (<see cref="ErrorCode.NotFound"/>, <see cref="ErrorCode.Conflict"/>, ...) from a remote unexpected failure
/// (<see cref="ErrorCode.InternalError"/>).
/// </summary>
public sealed class RemoteDispatchException : InvalidOperationException
{
    /// <summary>Creates the exception for a remote error.</summary>
    public RemoteDispatchException(ErrorCode code, string message, string? reference)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(code);
        this.Code = code;
        this.Reference = reference;
    }

    /// <summary>The remote error's code (<see cref="DispatchError.Code"/>) with the HTTP status its server declared
    /// (<see cref="DispatchError.Status"/>), or the built-in code's when the server predates it.</summary>
    public ErrorCode Code { get; }

    /// <summary>The remote failure's support reference (<see cref="DispatchError.Reference"/>), or null when it sent none.</summary>
    public string? Reference { get; }
}
