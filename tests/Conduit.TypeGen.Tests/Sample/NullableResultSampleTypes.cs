using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Sample;

// --- Result nullability: C# erases a reference type's `?`, so `IQuery<TestFound?>` and `IQuery<TestFound>` reflect
// identically. The compiler records the `?` in a NullableAttribute on the InterfaceImpl metadata row (or leaves it to
// the type's NullableContextAttribute), which ResultNullabilityReader decodes. The flags noted below are what the .NET 10
// compiler emits for this project, measured: the interface's own position (index 0) is always 0. ---

[ClientVisible]
public sealed record TestFound(int Id, string Name);

// byte[] form: [0 (the interface), 2 (the result)].
[ClientVisible]
public sealed record TestFindQuery(int Id) : IQuery<TestFound?>;

// Not null: [0, 1].
[ClientVisible]
public sealed record TestRequireQuery(int Id) : IQuery<TestFound>;

// A value-type result is Nullable<T>, which the type map already renders `number | null`. Single-byte form: 0.
[ClientVisible]
public sealed record TestCountOrNoneQuery : IQuery<int?>;

// The result's own flag is index 1 even when the result is generic: [0, 2 (the list), 1 (its element)].
[ClientVisible]
public sealed record TestFindManyQuery : IQuery<IReadOnlyList<TestFound>?>;

[ClientVisible]
public sealed record TestRenameCommand(int Id) : ICommand<TestFound?>;

// The InterfaceImpl row lives on the base record; the derived record has none for IQuery`1.
public abstract record TestFindBase : IQuery<TestFound?>;

[ClientVisible]
public sealed record TestDerivedFindQuery : TestFindBase;

// A nested query: its row is read through its own TypeDef ([0, 2]); a bare row would take its context from the
// nearest declaring type that has one.
public static class TestOuter
{
#pragma warning disable CA1034 // the nesting is what this sample tests
    [ClientVisible]
    public sealed record TestNestedFindQuery : IQuery<TestFound?>;
#pragma warning restore CA1034
}

// Mostly nullable members give the type NullableContext(2); the row's explicit [0, 1] must win over the context.
[ClientVisible]
public sealed record TestMostlyNullableQuery(string? A, string? B, string? C) : IQuery<TestFound>;

#nullable disable
// Oblivious: compiled with nullable disabled, so the result is neither nullable nor not-null -- emitted as today.
// Single-byte form: 0.
[ClientVisible]
public sealed record TestObliviousQuery : IQuery<TestFound>;
#nullable restore
