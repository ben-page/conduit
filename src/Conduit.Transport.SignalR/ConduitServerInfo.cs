namespace Conduit.Transport.SignalR;

/// <summary>
/// What a server tells each SignalR client about itself the moment it connects. Carried, never derived: Conduit does
/// not know how a host versions itself, so the host supplies it through <c>AddConduitSignalR</c>, and
/// <see cref="ConduitHub"/> sends it from <see cref="ConduitHub.OnConnectedAsync"/>.
/// </summary>
/// <param name="BuildId">The host's build id. Opaque to Conduit; a client compares it with the id it was built with.</param>
public sealed record ConduitServerInfo(string BuildId);
