using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

/// <summary>A command whose serialization throws (its getter does). Registered with <c>SuppressPayload&lt;T&gt;()</c>,
/// so capture must never serialize it.</summary>
public sealed class UnserializableCommand : ICommand
{
    public int Id { get; init; }

    public string Value => this.Id >= 0 ? throw new InvalidOperationException("This command cannot be serialized.") : string.Empty;
}
