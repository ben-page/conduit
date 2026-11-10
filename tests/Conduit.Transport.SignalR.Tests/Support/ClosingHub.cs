using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// A hub with <see cref="ConduitHub"/>'s <c>Dispatch</c> signature that answers an empty success, except that a
/// dispatch of the type <see cref="CloseType"/> aborts the caller's connection: the server going away under a client.
/// </summary>
public sealed class ClosingHub : Hub
{
    public const string CloseType = "Close";

    // SignalR rejects an arity mismatch, so the method declares all four of ConduitHub.Dispatch's parameters.
#pragma warning disable IDE0060
    public DispatchResult<object> Dispatch(
        string type, JsonElement payload, Dictionary<string, string>? context, Dictionary<string, string>? headers)
#pragma warning restore IDE0060
    {
        if (type == CloseType)
        {
            this.Context.Abort();
        }

        return DispatchResult<object>.Ok(null);
    }
}
