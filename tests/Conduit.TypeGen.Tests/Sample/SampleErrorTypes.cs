using Conduit.Exceptions;

namespace Conduit.TypeGen.Tests.Sample;

public sealed class SampleBlockedException(int total, IReadOnlyList<string> names, string message)
    : ConflictException(message)
{
    public int Total => total;

    public IReadOnlyList<string> Names => names;
}

/// <summary>Declares no client data but is still a consumer refusal (this assembly isn't Conduit.Contracts), so
/// it IS in the ErrorMap, with an empty <c>export interface</c>.</summary>
public sealed class SamplePlainException(string message) : ConflictException(message);
