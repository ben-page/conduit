using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class AsbReplyShould
{
    private static readonly ServiceBusReceivedMessage Request = ServiceBusModelFactory.ServiceBusReceivedMessage(
        replyTo: "conduit-orders-replies", replyToSessionId: "request-session");

    private static AzureServiceBusOptions CreateOptions(TimeSpan responseTimeout) => new()
    {
        ConnectionString = "not-used",
        ResponseTimeout = responseTimeout,
    };

    private static AzureServiceBusTransport CreateTransport(AzureServiceBusOptions options, IServiceProvider serviceProvider) =>
        new(options, new ConduitOptions(), serviceProvider, NullLogger<AzureServiceBusTransport>.Instance);

    private static CreateQueueOptions CreateReplyQueueOptions(AzureServiceBusOptions options)
    {
        var adminClient = new ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        var manager = new AzureServiceBusTopologyManager(
            adminClient, options, new ConduitOptions { ApplicationName = "orders" }, NullLogger.Instance);
        return manager.CreateReplyQueueOptions();
    }

    [TestMethod]
    public async Task CarryATimeToLive()
    {
        // Arrange -- a request whose sender may have given up by the time its reply is sent
        var options = CreateOptions(TimeSpan.FromSeconds(45));
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        await using var transport = CreateTransport(options, serviceProvider);

        // Act
        var reply = transport.CreateReplyMessage(Request, BinaryData.FromString("{}"));

        // Assert -- the reply goes to the request's session and expires after twice the response timeout,
        // rather than staying in the reply queue forever when nobody accepts that session
        reply.SessionId.Should().Be("request-session");
        reply.TimeToLive.Should().Be(TimeSpan.FromSeconds(90));
    }

    [TestMethod]
    [DataRow(300L, 1_000L, DisplayName = "300 ms: twice is under Service Bus's 1 s minimum, so 1 s")]
    [DataRow(500L, 1_000L, DisplayName = "500 ms: twice is exactly the 1 s minimum")]
    [DataRow(60_000L, 120_000L, DisplayName = "60 s: twice, 120 s")]
    public async Task ExpireAfterTwiceTheResponseTimeoutAndNoSoonerThanASecond(long responseTimeoutMs, long expectedMs)
    {
        // Arrange
        var options = CreateOptions(TimeSpan.FromMilliseconds(responseTimeoutMs));
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        await using var transport = CreateTransport(options, serviceProvider);

        // Act -- the reply and the reply queue, whose time to live Service Bus validates alike
        var reply = transport.CreateReplyMessage(Request, BinaryData.FromString("{}"));
        var replyQueueOptions = CreateReplyQueueOptions(options);

        // Assert
        var expected = TimeSpan.FromMilliseconds(expectedMs);
        reply.TimeToLive.Should().Be(expected);
        replyQueueOptions.DefaultMessageTimeToLive.Should().Be(expected);
    }

    [TestMethod]
    [DataRow(-10_000L, DisplayName = "Timeout.InfiniteTimeSpan")]
    [DataRow(0L, DisplayName = "TimeSpan.Zero")]
    [DataRow(long.MaxValue, DisplayName = "TimeSpan.MaxValue: too long to double")]
    public async Task CarryNoTimeToLiveWithoutAFiniteResponseTimeout(long responseTimeoutTicks)
    {
        // Arrange -- a response timeout with no finite double: infinite, not positive, or past half of MaxValue
        var options = CreateOptions(TimeSpan.FromTicks(responseTimeoutTicks));
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        await using var transport = CreateTransport(options, serviceProvider);

        // Act
        var reply = transport.CreateReplyMessage(Request, BinaryData.FromString("{}"));
        var replyQueueOptions = CreateReplyQueueOptions(options);

        // Assert -- neither the reply nor the reply queue expires anything: TimeSpan.MaxValue is Service Bus's "never"
        reply.TimeToLive.Should().Be(TimeSpan.MaxValue);
        replyQueueOptions.DefaultMessageTimeToLive.Should().Be(TimeSpan.MaxValue);
    }
}
