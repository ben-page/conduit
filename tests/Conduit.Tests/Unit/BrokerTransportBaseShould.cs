using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Messages;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Unit;

/// <summary>
/// How the base class settles a <c>[FireAndForget]</c> command it received: no caller waits for its result, so the
/// result decides between acknowledging, capturing and abandoning the message. And how it settles an event whose
/// dispatch failed outside its listeners (a pipeline behavior, the listeners' resolution, a gate): as a failed
/// listener's, never acknowledged as handled.
/// </summary>
[TestClass]
public partial class BrokerTransportBaseShould
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

    // Without a store the broker keeps it: abandoned, so its own dead-lettering applies. The abandon log names the
    // support reference the runtime logged the exception under, and does not repeat a stack
    [TestMethod]
    public async Task AbandonAFailedFireAndForgetCommandWhenNothingCaptures()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: false);
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var logger = new RecordingLogger<FakeBrokerTransport>();
        var transport = new FakeBrokerTransport(
            host.GetRequiredService<ConduitOptions>(), host.Services, logger: logger);
        var message = FireAndForgetMessage(new FailingFireAndForgetCommand("unexpected"));

        // Act
        await transport.DeliverAsync(message);

        // Assert
        spy.Count.Should().Be(1);
        message.Abandoned.Should().BeTrue();
        message.Acknowledged.Should().BeFalse();
        transport.Lifecycle.Should().BeEmpty();
        var abandoned = logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Should().ContainSingle().Subject;
        abandoned.EventId.Id.Should().Be(502);
        abandoned.Message.Should().MatchRegex("support reference: [0-9a-f]{32}");
        abandoned.Exception.Should().BeNull();
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
        var message = UnreadableFireAndForgetMessage();

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

    // Nothing to capture it: acknowledged all the same, never abandoned, since a redelivery would fail the same way
    [TestMethod]
    public async Task AcknowledgeAnUnreadableFireAndForgetCommandWhenNothingCaptures()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: false);
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetCommand>>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = UnreadableFireAndForgetMessage();

        // Act
        await transport.DeliverAsync(message);

        // Assert
        spy.Count.Should().Be(0);
        message.Acknowledged.Should().BeTrue();
        message.Abandoned.Should().BeFalse();
        transport.Lifecycle.Should().Equal("acknowledged");
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

    // A behavior wraps the listeners, outside their own try: its failure is no refusal and no handled event, so the
    // event is kept as a failed listener's is -- captured with the support reference the runtime logged it under,
    // the broker told once, by the capture
    [TestMethod]
    public async Task CaptureAWireEventWhoseBehaviorThrows()
    {
        // Arrange
        await using var host = StartHost(
            withInterceptor: true,
            conduit => conduit.AddPipeline(typeof(ThrowingEventBehavior<,>)));
        var interceptor = host.GetRequiredService<RecordingErrorInterceptor>();
        var logger = new RecordingLogger<FakeBrokerTransport>();
        var transport = new FakeBrokerTransport(
            host.GetRequiredService<ConduitOptions>(), host.Services, logger: logger);
        var message = EventMessage(new BehaviorFailureEvent("x"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- captured with a reference, acknowledged once by the capture, never completed as handled
        var capture = interceptor.Captures.Should().ContainSingle().Subject;
        capture.MessageType.Should().Be(typeof(BehaviorFailureEvent).FullName);
        capture.Reference.Should().NotBeNullOrEmpty();
        transport.Lifecycle.Should().Equal("acknowledged");
        message.AcknowledgeAttempts.Should().Be(1);
        message.Abandoned.Should().BeFalse();
        logger.Entries.Should().NotContain(entry => entry.EventId.Id == 501, "the event was not handled");
    }

    // A gate that throws something other than a refusal (an inbox whose store is down) decided nothing: no listener
    // ran and nothing recorded the event, so the broker must deliver it again. The runtime's Error line carries the
    // stack under a support reference; the abandon names that reference and does not repeat the stack
    [TestMethod]
    public async Task AbandonAWireEventWhoseGateThrowsWhenNothingCaptures()
    {
        // Arrange
        var spy = new SpyLoggerProvider();
        await using var host = StartHost(
            withInterceptor: false,
            configureBuilder: builder =>
            {
                builder.Logging.AddProvider(spy);
                builder.Services.AddScoped<IInboundInterceptor, ThrowingGate>();
            });
        var logger = new RecordingLogger<FakeBrokerTransport>();
        var transport = new FakeBrokerTransport(
            host.GetRequiredService<ConduitOptions>(), host.Services, logger: logger);
        var message = EventMessage(new SimpleEvent("x"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- abandoned, never acknowledged
        message.Abandoned.Should().BeTrue();
        message.Acknowledged.Should().BeFalse();
        transport.Lifecycle.Should().BeEmpty();
        var abandoned = logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Should().ContainSingle().Subject;
        abandoned.EventId.Id.Should().Be(502);
        abandoned.Exception.Should().BeNull();
        var reference = SupportReference().Match(abandoned.Message).Groups[1].Value;
        reference.Should().NotBeEmpty();
        var failure = spy.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Error).Subject;
        failure.EventId.Id.Should().Be(2);
        failure.EventId.Name.Should().Be("PublishError");
        failure.Message.Should().Contain(reference);
        failure.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("The inbox store is unreachable.");
    }

    // The listeners are resolved inside the behaviors and outside each listener's own try: one that cannot be built
    // fails the dispatch before any listener runs, and the event is settled as a failed listener's is
    [TestMethod]
    public async Task AbandonAWireEventWhoseListenerCannotBeBuiltWhenNothingCaptures()
    {
        // Arrange
        await using var host = StartHost(withInterceptor: false);
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var message = EventMessage(new UnbuildableListenerEvent("x"));

        // Act
        await transport.DeliverAsync(message);

        // Assert -- abandoned, so the broker redelivers or dead-letters it, never acknowledged as handled
        message.Abandoned.Should().BeTrue();
        message.Acknowledged.Should().BeFalse();
        transport.Lifecycle.Should().BeEmpty();
    }

    private static TestWebHost StartHost(
        bool withInterceptor,
        Action<ConduitBuilder>? configure = null,
        Action<WebApplicationBuilder>? configureBuilder = null) =>
        TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<FailingFireAndForgetCommand>>();
            builder.Services.AddSingleton<RecordingErrorInterceptor>();
            if (withInterceptor)
            {
                builder.Services.AddSingleton<IInboundErrorInterceptor>(
                    provider => provider.GetRequiredService<RecordingErrorInterceptor>());
            }

            configureBuilder?.Invoke(builder);
            builder.Services.AddConduit(configure);
        });

    // The reference an abandon log names (MessageAbandoned's template)
    [GeneratedRegex("support reference: ([0-9a-f]{32})")]
    private static partial Regex SupportReference();

    // As a broker delivers a published event: no reply address
    private static FakeBrokerMessage EventMessage(IEvent @event)
    {
        var envelope = TestEnvelope.For(@event);
        return new FakeBrokerMessage { Type = envelope.Type, Body = new BinaryData(envelope.Payload.GetRawText()) };
    }

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

    // Valid JSON with a number where the command has a string: a BadRequest refusal
    private static FakeBrokerMessage UnreadableFireAndForgetMessage() => new()
    {
        Type = typeof(FailingFireAndForgetCommand).FullName!,
        Body = new BinaryData("""{"reason":42}"""),
    };
}
