using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

/// <summary>
/// The base class's dead-letter capture, without a broker. The per-broker
/// <c>{Transport}DeadLetterShould</c> classes prove the same against the real ones.
/// </summary>
[TestClass]
public class BrokerTransportDeadLetterShould
{
    // A broker rejects a second settlement of one delivery: RabbitMQ closes the channel (an unknown
    // delivery tag), Azure Service Bus throws for a lost lock.
    [TestMethod]
    public async Task AcknowledgeACapturedRequestOnce()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<RecordingErrorInterceptor>();
            builder.Services.AddSingleton<IInboundErrorInterceptor>(
                provider => provider.GetRequiredService<RecordingErrorInterceptor>());
            builder.Services.AddConduit();
        });
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var envelope = TestEnvelope.ForRequest(new FailingCommand("unexpected"));
        var message = new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(envelope.Payload.GetRawText()),
            IsRequest = true,
        };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- captured with its support reference, then acknowledged exactly once
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(FailingCommand).FullName);
        capture.Reference.Should().NotBeNullOrEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
    }
}
