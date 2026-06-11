using System.Text.Json;
using Conduit.Attributes;

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
