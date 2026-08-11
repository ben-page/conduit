using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Sample;

// --- Fix 1: generic result types (PagedResult<T>) must emit `PagedResult<...>`, not `PagedResult`1`. ---

[ClientVisible]
public sealed record TestPagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

[ClientVisible]
public sealed record TestActivityListItem
{
    public Guid Id { get; init; }
    public string Description { get; init; } = string.Empty;
}

// A DTO with a closed-generic property, so the emitter reflects over a resolved
// PagedResult<Item> Type and routes it through TypeMapper.MapType (Fix 1's target path).
[ClientVisible]
public sealed record TestActivityPageDto
{
    public TestPagedResult<TestActivityListItem> Results { get; init; } = new();
}

// A query returning a closed generic result, so the QueryMap `response` is produced via
// TypeMapEmitter.ResolveResultType → TypeMapper.MapFullTypeName (Fix 1's map-path target).
[ClientVisible]
public sealed record TestListActivitiesQuery(int Page) : IQuery<TestPagedResult<TestActivityListItem>>;

// --- Fix 2: System.Text.Json.JsonElement must emit `unknown`. ---

[ClientVisible]
public sealed record TestRawPayloadDto
{
    public JsonElement Payload { get; init; }
}

// --- Fix 3: nullable reference types must emit `string | null`; non-null stays `string`. ---

[ClientVisible]
public sealed record TestNullabilityDto
{
    public string Required { get; init; } = string.Empty;
    public string? Optional { get; init; }
}

// --- Fix 4: a query result of IReadOnlyList<TEnum> must emit an inline string-literal union
// array (('A' | 'B')[]), never the bare enum type name -- there is no `TestActivity` interface
// or type alias on the TS side, so a bare name is a compile error, not merely an imprecise type. ---

public enum TestActivity
{
    Run,
    Idle,
    TearDown,
}

[ClientVisible]
public sealed record TestListActivityOptionsQuery(int OrderId) : IQuery<IReadOnlyList<TestActivity>>;
