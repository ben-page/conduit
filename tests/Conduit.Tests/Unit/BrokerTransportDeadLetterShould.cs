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
        await using var host = StartHost();
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = RequestMessage(new FailingCommand("unexpected"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- captured with its support reference, then acknowledged exactly once
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(FailingCommand).FullName);
        capture.Reference.Should().NotBeNullOrEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
    }

    // The store holds the message, so the broker must not be told twice: an acknowledgement that fails after a
    // capture is not a store failure, and neither a second acknowledgement nor an abandon may follow it.
    [TestMethod]
    public async Task SettleACaptureOnceWhenItsAcknowledgementFails()
    {
        // Arrange
        await using var host = StartHost();
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = RequestMessage(
            new FailingCommand("unexpected"),
            acknowledgeFailure: new InvalidOperationException("The channel is closed."));

        // Act
        var deliver = () => transport.DeliverAsync(message);

        // Assert -- captured, one acknowledgement attempted, its failure left to the broker client
        await deliver.Should().ThrowAsync<InvalidOperationException>().WithMessage("The channel is closed.");
        interceptor.Captures.Should().ContainSingle();
        message.AcknowledgeAttempts.Should().Be(1);
        message.Abandoned.Should().BeFalse();
    }

    // Valid JSON its type cannot read is no refusal of its publisher: no redelivery could ever dispatch it.
    [TestMethod]
    public async Task CaptureAnEventItsTypeCannotRead()
    {
        // Arrange
        await using var host = StartHost();
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = new FakeBrokerMessage
        {
            Type = typeof(SimpleEvent).FullName!,
            Body = new BinaryData("""{"data":42}"""),
        };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- captured as a bad request, acknowledged once by the capture
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(SimpleEvent).FullName);
        capture.Reference.Should().BeNull();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
    }

    // A refusal by a gate (the type filter's 403) is final, and a replay would be refused again.
    [TestMethod]
    public async Task NotCaptureAForbiddenEvent()
    {
        // Arrange
        await using var host = StartHost(conduit =>
            conduit.AddTypeFilter(filter => filter.AllowType(typeof(ItemCreatedEvent).FullName!)));
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var envelope = TestEnvelope.For(new SimpleEvent("not allowed"));
        var message = new FakeBrokerMessage { Type = envelope.Type, Body = new BinaryData(envelope.Payload.GetRawText()) };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- acknowledged, nothing stored
        interceptor.Captures.Should().BeEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
    }

    // The dead letter queue stores a failure and the inbox releases its record: every interceptor registered is
    // offered the failure, not only the last one, and the message is still settled once.
    [TestMethod]
    public async Task OfferAFailureToEveryErrorInterceptor()
    {
        // Arrange
        var first = new RecordingErrorInterceptor();
        var second = new RecordingErrorInterceptor();
        await using var host = StartHostWith(first, second);
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = RequestMessage(new FailingCommand("unexpected"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- both offered it, one acknowledgement
        first.Captures.Should().ContainSingle();
        second.Captures.Should().ContainSingle();
        transport.Lifecycle.Should().Equal("acknowledged");
    }

    // One interceptor's failure is its own: those before and after it still run, and a capture by any of them still
    // settles the message (acknowledged, where an uncaptured listener failure is abandoned).
    [TestMethod]
    public async Task OfferAFailureToTheOtherErrorInterceptorsWhenOneThrows()
    {
        // Arrange
        var before = new RecordingErrorInterceptor();
        var after = new RecordingErrorInterceptor();
        await using var host = StartHostWith(before, new ThrowingErrorInterceptor(), after);
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var envelope = TestEnvelope.For(new InlineFailureEvent("x"));
        var message = new FakeBrokerMessage { Type = envelope.Type, Body = new BinaryData(envelope.Payload.GetRawText()) };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- offered to both that did not throw, acknowledged once, not abandoned
        before.Captures.Should().ContainSingle().Which.MessageType.Should().Be(typeof(InlineFailureEvent).FullName);
        after.Captures.Should().ContainSingle().Which.MessageType.Should().Be(typeof(InlineFailureEvent).FullName);
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
    }

    private static TestWebHost StartHost(Action<ConduitBuilder>? configure = null) => TestWebHost.Start(builder =>
    {
        builder.Services.AddSingleton<RecordingErrorInterceptor>();
        builder.Services.AddSingleton<IInboundErrorInterceptor>(
            provider => provider.GetRequiredService<RecordingErrorInterceptor>());
        builder.Services.AddConduit(configure);
    });

    private static TestWebHost StartHostWith(params IInboundErrorInterceptor[] interceptors) => TestWebHost.Start(builder =>
    {
        foreach (var interceptor in interceptors)
        {
            builder.Services.AddSingleton(interceptor);
        }

        builder.Services.AddSingleton<EventSpy<string>>();
        builder.Services.AddConduit();
    });

    private static FakeBrokerMessage RequestMessage(object request, Exception? acknowledgeFailure = null)
    {
        var envelope = TestEnvelope.ForRequest(request);
        return new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(envelope.Payload.GetRawText()),
            IsRequest = true,
            AcknowledgeFailure = acknowledgeFailure,
        };
    }
}
