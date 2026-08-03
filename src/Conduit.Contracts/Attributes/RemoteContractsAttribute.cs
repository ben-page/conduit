namespace Conduit.Attributes;

/// <summary>
/// Declares that command and query types in the specified namespace are remote contracts
/// handled by another service. Suppresses CONDUIT001 (no handler found) for matching types.
/// </summary>
/// <example>
/// <code>
/// [assembly: RemoteContracts("Conduit.Sample.Inventory")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RemoteContractsAttribute(string namespacePattern) : Attribute
{
    /// <summary>
    /// The namespace prefix to match. Types whose fully-qualified name starts with this
    /// pattern are treated as remote contracts and will not produce CONDUIT001 warnings.
    /// </summary>
    public string NamespacePattern => namespacePattern;
}
