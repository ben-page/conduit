using Conduit.Exceptions;

namespace Conduit.TypeGen.Tests.Sample;

public sealed class SampleBlockedException(int total, IReadOnlyList<string> names, string message)
    : ConflictException(message)
{
    public int Total => total;

    public IReadOnlyList<string> Names => names;
}

/// <summary>Declares nothing, so it is NOT in the ErrorMap.</summary>
public sealed class SamplePlainException(string message) : ConflictException(message);
