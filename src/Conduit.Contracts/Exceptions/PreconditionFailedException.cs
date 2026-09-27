namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the version the caller acted on is no longer current — an optimistic-concurrency token
/// (ETag) that no longer matches. The caller should refresh and retry. Maps to HTTP 412 Precondition
/// Failed. A request that collides with the resource's STATE, where a refresh would not make the same
/// request succeed, is a <see cref="ConflictException"/> instead.
/// Not sealed: subclass it for a refusal a client acts on, optionally declaring its data as public
/// properties (<see cref="ConduitException.GetClientData"/>); a data-less subclass declared outside
/// Conduit.Contracts is still client-typed.
/// </summary>
public class PreconditionFailedException(string message)
    : ConduitException(ErrorCode.PreconditionFailed, message);
