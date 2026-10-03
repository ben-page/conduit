namespace Conduit.Runtime;

/// <summary>
/// A remote endpoint answered a dispatch with an error. Thrown by <see cref="ConduitRuntime.UnwrapReply"/>; still an
/// <see cref="InvalidOperationException"/> so existing catches keep working.
/// </summary>
public sealed class RemoteDispatchException : InvalidOperationException
{
    /// <summary>Creates the exception for a remote error.</summary>
    public RemoteDispatchException(string message, string? reference)
        : base(message) => this.Reference = reference;

    /// <summary>The remote failure's support reference (<see cref="DispatchError.Reference"/>), or null when it sent none.</summary>
    public string? Reference { get; }
}
