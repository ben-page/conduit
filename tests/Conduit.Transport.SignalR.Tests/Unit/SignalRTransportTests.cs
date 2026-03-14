using AwesomeAssertions;
using Conduit.Transport.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Conduit.Transport.SignalR.Tests.Unit;

[TestClass]
public class SignalRTransportShould
{
    [TestMethod]
    public async Task RejectNullEnvelopeOnSendRequest()
    {
        // Arrange
        var hubConnection = new HubConnectionBuilder()
            .WithUrl("http://localhost:1")
            .Build();
        var transport = new SignalRTransport(hubConnection);

        // Act
        var action = () => transport.SendRequestAsync(null!, CancellationToken.None);

        // Assert
        await action.Should().ThrowExactlyAsync<ArgumentNullException>();
        await transport.DisposeAsync();
    }

    [TestMethod]
    public async Task RejectNullEnvelopeOnSendEvent()
    {
        // Arrange
        var hubConnection = new HubConnectionBuilder()
            .WithUrl("http://localhost:1")
            .Build();
        var transport = new SignalRTransport(hubConnection);

        // Act
        var action = () => transport.SendEventAsync(null!, CancellationToken.None);

        // Assert
        await action.Should().ThrowExactlyAsync<ArgumentNullException>();
        await transport.DisposeAsync();
    }
}
