using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

public abstract record UnannotatedBase
{
    public string InheritedField { get; init; } = string.Empty;
}

[ClientVisible]
public sealed record AnnotatedDerived : UnannotatedBase
{
    public int OwnField { get; init; }
}
