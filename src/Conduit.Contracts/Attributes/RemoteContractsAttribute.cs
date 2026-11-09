namespace Conduit.Attributes;

/// <summary>
/// Declares that command and query types in the specified namespace (and the namespaces beneath it) are remote
/// contracts handled by another service. Suppresses CONDUIT001 (no handler found) for matching types.
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
    /// The namespace to match, on whole segments: types in it and in the namespaces beneath it are treated as remote
    /// contracts and will not produce CONDUIT001 warnings (<c>"Inventory"</c> covers <c>Inventory.Commands</c>, not
    /// <c>InventoryAudit</c>). A type's full name matches that one type.
    /// </summary>
    public string NamespacePattern => namespacePattern;
}
