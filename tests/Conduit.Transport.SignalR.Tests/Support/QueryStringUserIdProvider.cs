using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>Names a connection's user by its <c>user</c> query parameter, so a test can connect as two users without auth.</summary>
public sealed class QueryStringUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.GetHttpContext()?.Request.Query["user"].ToString();
    }
}
