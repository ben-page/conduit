namespace Conduit.Transport.SignalR;

/// <summary>
/// Options for the inbound side of the SignalR transport: how <see cref="ConduitHub"/> treats its callers. Set through
/// <see cref="ServiceCollectionExtensions.AddConduitSignalR(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{ConduitSignalROptions}?)"/>
/// and read as <c>IOptions&lt;ConduitSignalROptions&gt;</c>. The outbound, client-mode transport is configured by
/// <see cref="SignalROptions"/> instead.
/// </summary>
public sealed class ConduitSignalROptions
{
    /// <summary>
    /// Whether the hub honors the correlation ids (<c>MessageId</c>, <c>ConversationId</c>, <c>CausationId</c>) a caller
    /// passes in the <c>context</c> argument of <see cref="ConduitHub.Dispatch"/> and <see cref="ConduitHub.Publish"/>.
    /// False by default: the hub faces browsers, the inbox deduplicates on the MessageId and audit lineage follows the
    /// other two, so the server mints fresh ids for every invocation. Set it only for a hub that service-to-service
    /// callers (<see cref="SignalRTransport"/> in client mode) reach and browsers do not. Even then the ids are honored
    /// only when the message and conversation ids are both present and every supplied id is a plain token
    /// (<see cref="Conduit.Runtime.MessageContextIds.IsValid"/>); otherwise the hub mints fresh ones.
    /// </summary>
    public bool TrustCallerContext { get; set; }
}
