namespace Conduit.Analysis;

public sealed class DispatchableTypeInfo
{
    public required string TypeName { get; init; }
    public required string FullTypeName { get; init; }
    public required string Namespace { get; init; }
    public required DispatchableKind Kind { get; init; }
    public string? ResultTypeName { get; init; }
    public string? ResultFullTypeName { get; init; }
    public required bool HasValidateOverride { get; init; }
    public required bool HasAuthorizeAttribute { get; init; }
    public required bool HasAllowAnonymousAttribute { get; init; }
}
