using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

/// <summary>A command whose handler waits on the host's <see cref="AuditCommandGate"/>, observing nothing meanwhile.</summary>
public sealed record HeldAuditCommand(string Value) : ICommand<string>;
