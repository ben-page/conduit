using Conduit.Runtime;

namespace Conduit.Exceptions;

/// <summary>
/// Thrown when an inbound wire request is missing a primary-constructor parameter that has no default -- the
/// sender was built against an older contract (an out-of-date browser tab). Maps to HTTP 400 via
/// <see cref="ErrorCode.ContractMismatch"/>. Silently defaulting the member would let a stale client clear a field
/// it does not know about, so the request is refused instead. Carries one <see cref="DispatchErrorDetail"/> per
/// missing member (<c>Field</c> = the wire name, <c>Message</c> = <c>"missing"</c>).
/// </summary>
public sealed class ContractMismatchException(IReadOnlyList<string> missing)
    : ConduitException(
        ErrorCode.ContractMismatch,
        $"This page is out of date: the request is missing {string.Join(", ", missing)}. Reload to continue.",
        [.. missing.Select(m => new DispatchErrorDetail(m, "missing"))]);
