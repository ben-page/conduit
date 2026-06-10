using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Generated;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// Verifies that the wire-inbound dispatch path preserves the transport-supplied
/// MessageContext (MessageId / CausationId / ConversationId) instead of minting a
/// fresh context. Transports establish the context via
/// <see cref="MessageContext.StartDispatchFromTransport"/> before invoking
/// <see cref="IWireMediator.HandleWireRequestAsync"/> /
/// <see cref="IWireMediator.HandleWireEventAsync"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class WireInboundContextShould
{
    private ServiceProvider? serviceProvider;

    [TestInitialize]
    public void Setup()
    {
        CapturedContext.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit =>
        {
            conduit.AddPipeline(typeof(ContextCapturingBehavior<,>));
        });
        this.serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
        this.serviceProvider?.Dispose();
    }

    [TestMethod]
    public async Task PreserveTransportSuppliedContextForWireRequest()
    {
        // Arrange — mirror what a transport does before calling the wire mediator
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        var wireMessageId = "wire-message-id-1234";
        var wireCausationId = "wire-causation-id-5678";
        var wireConversationId = "wire-conversation-id-9012";
        var envelope = new DispatchEnvelope(
            typeof(EchoCommand).FullName!,
            JsonSerializer.SerializeToElement(new { Value = "hello" }));

        MessageContext.StartDispatchFromTransport(wireMessageId, wireCausationId, wireConversationId);

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
        var captured = CapturedContext.ForType(typeof(EchoCommand).FullName!);
        captured.Should().NotBeNull();
        captured!.MessageId.Should().Be(wireMessageId);
        captured.CausationId.Should().Be(wireCausationId);
        captured.ConversationId.Should().Be(wireConversationId);
    }

    [TestMethod]
    public async Task PreserveTransportSuppliedContextForWireEvent()
    {
        // Arrange — mirror what a transport does before calling the wire mediator
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        var wireMessageId = "wire-event-message-id-1234";
        var wireCausationId = "wire-event-causation-id-5678";
        var wireConversationId = "wire-event-conversation-id-9012";
        var envelope = new DispatchEnvelope(
            typeof(NestedEventOne).FullName!,
            JsonSerializer.SerializeToElement(new { Value = "hello" }));

        MessageContext.StartDispatchFromTransport(wireMessageId, wireCausationId, wireConversationId);

        // Act
        await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert
        var captured = CapturedContext.ForType(typeof(NestedEventOne).FullName!);
        captured.Should().NotBeNull();
        captured!.MessageId.Should().Be(wireMessageId);
        captured.CausationId.Should().Be(wireCausationId);
        captured.ConversationId.Should().Be(wireConversationId);
    }

    [TestMethod]
    public async Task MintFreshContextForWireRequestWhenNoTransportContext()
    {
        // Arrange — no transport pre-established a context (e.g. an in-process caller)
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            typeof(EchoCommand).FullName!,
            JsonSerializer.SerializeToElement(new { Value = "hello" }));

        MessageContext.Current.Should().BeNull();

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert — a context is still established for the handler/pipeline
        result.Error.Should().BeNull();
        var captured = CapturedContext.ForType(typeof(EchoCommand).FullName!);
        captured.Should().NotBeNull();
        captured!.MessageId.Should().NotBeNullOrEmpty();
        captured.ConversationId.Should().Be(captured.MessageId);
        captured.CausationId.Should().Be(captured.MessageId);
    }
}
