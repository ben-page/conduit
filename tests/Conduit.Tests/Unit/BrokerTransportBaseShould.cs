using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Unit;

/// <summary>
/// How the base class settles a <c>[FireAndForget]</c> command it received: no caller waits for its result, so the
/// result decides between acknowledging, capturing and abandoning the message.
/// </summary>
[TestClass]
public class BrokerTransportBaseShould
{
    // An unexpected failure is kept, as an event listener's is: the dead-letter store takes it and the broker is told once
    [TestMethod]
    public async Task CaptureAFailedFireAndForgetCommandWhenAnInterceptorIsRegistered()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: true);
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = FireAndForgetMessage(new FailingFireAndForgetCommand("unexpected"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- the handler ran, its failure was captured with its support reference, acknowledged once
        spy.Count.Should().Be(1);
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(FailingFireAndForgetCommand).FullName);
        capture.Reference.Should().NotBeNullOrEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
    }

    // Without a store the broker keeps it: abandoned, so its own dead-lettering applies
    [TestMethod]
    public async Task AbandonAFailedFireAndForgetCommandWhenNothingCaptures()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: false);
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = FireAndForgetMessage(new FailingFireAndForgetCommand("unexpected"));

        // Act
        await transport.DeliverAsync(message);

        // Assert
        spy.Count.Should().Be(1);
        message.Abandoned.Should().BeTrue();
        message.Acknowledged.Should().BeFalse();
        transport.Lifecycle.Should().BeEmpty();
    }

    // Valid JSON its type cannot read: no redelivery could dispatch it, so it is settled as an unreadable event is
    [TestMethod]
    public async Task SettleAnUnreadableFireAndForgetCommandAsUndispatchable()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: true);
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = new FakeBrokerMessage
        {
            Type = typeof(FailingFireAndForgetCommand).FullName!,
            Body = new BinaryData("""{"reason":42}"""),
        };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- never handled, captured without a reference, acknowledged once by the capture
        spy.Count.Should().Be(0);
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(FailingFireAndForgetCommand).FullName);
        capture.Reference.Should().BeNull();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
    }

    // A refusal is final and no caller waits for it: acknowledged, said once at Warning, never stored
    [TestMethod]
    public async Task AcknowledgeARefusedFireAndForgetCommandWithAWarning()
    {
        // Arrange
        await using var host = StartHost(
            withInterceptor: true,
            conduit => conduit.AddTypeFilter(filter => filter.AllowType(typeof(ImportDataCommand).FullName!)));
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var logger = new RecordingLogger<FakeBrokerTransport>();
        var transport = new FakeBrokerTransport(
            host.GetRequiredService<ConduitOptions>(), host.Services, logger: logger);
        var message = FireAndForgetMessage(new FailingFireAndForgetCommand("not allowed"));

        // Act
        await transport.DeliverAsync(message);

        // Assert
        spy.Count.Should().Be(0);
        interceptor.Captures.Should().BeEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.Abandoned.Should().BeFalse();
        var warning = logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.EventId.Id.Should().Be(511);
        warning.EventId.Name.Should().Be("FireAndForgetCommandRefused");
        warning.Message.Should().Contain(typeof(FailingFireAndForgetCommand).FullName).And.Contain("Forbidden");
        warning.Exception.Should().BeNull();
    }

    private static TestWebHost StartHost(bool withInterceptor, Action<ConduitBuilder>? configure = null) =>
        TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<FailingFireAndForgetCommand>>();
            builder.Services.AddSingleton<RecordingErrorInterceptor>();
            if (withInterceptor)
            {
                builder.Services.AddSingleton<IInboundErrorInterceptor>(
                    provider => provider.GetRequiredService<RecordingErrorInterceptor>());
            }

            builder.Services.AddConduit(configure);
        });

    // As a routed [FireAndForget] command arrives: sent with SendEventAsync, so no reply address
    private static FakeBrokerMessage FireAndForgetMessage(object command)
    {
        var envelope = TestEnvelope.ForRequest(command);
        return new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, ConduitRuntime.JsonOptions)),
        };
    }
}
