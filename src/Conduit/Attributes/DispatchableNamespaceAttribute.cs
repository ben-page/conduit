namespace Conduit.Attributes;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DispatchableNamespaceAttribute(string ns) : Attribute
{
    public string Namespace => ns;
}
