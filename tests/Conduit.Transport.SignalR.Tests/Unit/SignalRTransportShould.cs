using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Transport.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

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

    // UseSignalR used to register the instance it built, which the container never disposes: the HubConnection outlived
    // the host. Registered through a keyed factory, the container owns it.
    [TestMethod]
    public async Task BeDisposedWithTheContainer()
    {
        // Arrange
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddEndpoint(
            "inventory", endpoint => endpoint.UseSignalR(new Uri("http://127.0.0.1:1/conduit")));
        var serviceProvider = services.BuildServiceProvider();
        var transport = serviceProvider.GetRequiredKeyedService<SignalRTransport>("inventory");
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        // Act
        await serviceProvider.DisposeAsync();

        // Assert
        var action = () => transport.SendRequestAsync(envelope, CancellationToken.None);
        await action.Should().ThrowAsync<ObjectDisposedException>();
    }
}
