using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// A hub with <see cref="ConduitHub"/>'s <c>Dispatch</c> signature that records the raw <c>context</c> argument and
/// answers an empty success, so a test sees what a client-mode <see cref="SignalRTransport"/> sent before any server-side
/// trust decision could hide it.
/// </summary>
public sealed class ContextRecordingHub(RecordedHubContexts recorded) : Hub
{
    // SignalR rejects an arity mismatch, so the method declares all four of ConduitHub.Dispatch's parameters.
#pragma warning disable IDE0060
    public DispatchResult<object> Dispatch(
        string type, JsonElement payload, Dictionary<string, string>? context, Dictionary<string, string>? headers)
#pragma warning restore IDE0060
    {
        recorded.Add(context);
        return DispatchResult<object>.Ok(null);
    }
}
