using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class AsbReplyShould
{
    [TestMethod]
    public async Task CarryATimeToLive()
    {
        // Arrange -- a request whose sender may have given up by the time its reply is sent
        var options = new AzureServiceBusOptions
        {
            ConnectionString = "not-used",
            ResponseTimeout = TimeSpan.FromSeconds(45),
        };
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        await using var transport = new AzureServiceBusTransport(
            options, new ConduitOptions(), serviceProvider, NullLogger<AzureServiceBusTransport>.Instance);
        var request = ServiceBusModelFactory.ServiceBusReceivedMessage(
            replyTo: "conduit-orders-replies", replyToSessionId: "request-session");

        // Act
        var reply = transport.CreateReplyMessage(request, BinaryData.FromString("{}"));

        // Assert -- the reply goes to the request's session and expires after twice the response timeout,
        // rather than staying in the reply queue forever when nobody accepts that session
        reply.SessionId.Should().Be("request-session");
        reply.TimeToLive.Should().Be(TimeSpan.FromSeconds(90));
    }
}
