using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class SignalRTransportShould
{
    private static TestWebHost CreateHandlerHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static TestWebHost CreateCallerHost(string remoteAddress)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                    {
                        endpoint.UseSignalR(new Uri($"{remoteAddress}/conduit"));
                        endpoint.RouteNamespace<CreateItemCommand>();
                    });
                });
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    [TestMethod]
    public async Task RoundTripCommandWithResult()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("SignalR Test"));

        // Assert — verify the item was created on the HANDLER host, not the caller
        id.Should().NotBeEmpty();
        handlerHost.GetRequiredService<ItemStore>().Get(id).Should().Be("SignalR Test");
    }

    [TestMethod]
    public async Task RoundTripVoidCommand()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var store = handlerHost.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "To Delete");
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        store.Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripQuery()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        handlerHost.GetRequiredService<ItemStore>().Add(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Query Test");

        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task PropagateNotFoundException()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new GetItemQuery(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NotFound*");
    }

    [TestMethod]
    public async Task PropagateGenericExceptionAsInternalError()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new ExplodingCommand());

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*InternalError*");
    }

    // The carried ruling: a routed [FireAndForget] command goes out with SendEventAsync, and a client-mode transport
    // must invoke the hub's Dispatch for it (the reply discarded), not Publish, whose event switch refuses a command as
    // an unknown message type. The hub runs the handler inline, so it has run by the time the send returns.
    [TestMethod]
    public async Task DispatchAFireAndForgetCommandThroughTheHubsDispatchMethod()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var store = handlerHost.GetRequiredService<ItemStore>();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();
        var id = Guid.NewGuid();

        // Act
        await mediator.ExecuteAsync(new NoteItemCommand(id, "Noted"));

        // Assert
        store.Get(id).Should().Be("Noted");
        callerHost.GetRequiredService<ItemStore>().Get(id).Should().BeNull();
    }

    // A reply-less send of an event (the host's registry says so) goes to the hub's Publish, whose listeners run on the
    // remote host.
    [TestMethod]
    public async Task PublishAnEventThroughTheHubsPublishMethod()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var spy = handlerHost.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var registry = new ConduitTypeRegistry(eventTypes: [typeof(ItemCreatedEvent)], commandTypes: [], queryTypes: []);
        await using var transport = new SignalRTransport(
            new HubConnectionBuilder().WithUrl($"{handlerHost.Address}/conduit").Build(), registry);
        var id = Guid.NewGuid();
        var envelope = new DispatchEnvelope(
            typeof(ItemCreatedEvent).FullName!,
            JsonSerializer.SerializeToElement(new ItemCreatedEvent(id, "Published"), ConduitRuntime.JsonOptions));

        // Act
        await transport.SendEventAsync(envelope, CancellationToken.None);

        // Assert
        await spy.WaitForCountAsync(1);
        spy.Events.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    private static TestWebHost CreateClosingHost()
    {
        return TestWebHost.Start(
            builder => builder.Services.AddSignalR(),
            app => app.MapHub<ClosingHub>("/closing"));
    }

    // Automatic reconnect gives up (here at once: no retry delays) and the connection is Disconnected for good. The
    // transport used to remember it had connected once and fail every later send until the process restarted.
    [TestMethod]
    public async Task ReconnectAfterTheConnectionClosedForGood()
    {
        // Arrange
        await using var host = CreateClosingHost();
        var hubConnection = new HubConnectionBuilder()
            .WithUrl($"{host.Address}/closing")
            .WithAutomaticReconnect([])
            .Build();
        await using var transport = new SignalRTransport(hubConnection);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hubConnection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        var close = new DispatchEnvelope(ClosingHub.CloseType, JsonSerializer.SerializeToElement(new { }));
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));
        await transport.SendRequestAsync(envelope, CancellationToken.None);
        try
        {
            await transport.SendRequestAsync(close, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            // The invocation dies with its connection.
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var action = () => transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert
        await action.Should().NotThrowAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
    }

    private static TestWebHost CreateRecordingHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<RecordedHubContexts>();
                builder.Services.AddSignalR();
            },
            app => app.MapHub<ContextRecordingHub>("/recording"));
    }

    [TestMethod]
    public async Task ForwardAContextWhoseIdsArePlainTokens()
    {
        // Arrange
        await using var host = CreateRecordingHost();
        var recorded = host.GetRequiredService<RecordedHubContexts>();
        await using var transport = new SignalRTransport(
            new HubConnectionBuilder().WithUrl($"{host.Address}/recording").Build());
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));
        MessageContext.StartDispatchFromTransport("m-1", "p-1", "c-1");

        // Act
        await transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert
        recorded.Received.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["MessageId"] = "m-1",
            ["ConversationId"] = "c-1",
            ["CausationId"] = "p-1",
        });
    }

    // A broker can hand this host any id, and the handler may route a command onward over SignalR. The context is
    // forwarded only when all three ids are plain tokens, as a trusting hub would honor it; otherwise none is sent and
    // the receiving hub mints, whether it trusts its callers or not.
    [TestMethod]
    public async Task NotForwardAContextThatIsNotPlainTokens()
    {
        // Arrange
        await using var host = CreateRecordingHost();
        var recorded = host.GetRequiredService<RecordedHubContexts>();
        await using var transport = new SignalRTransport(
            new HubConnectionBuilder().WithUrl($"{host.Address}/recording").Build());
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));
        (string MessageId, string CausationId, string ConversationId)[] contexts =
        [
            ("m-1\r\nforged log line", "p-1", "c-1"),
            ("m-1", "p-1", new string('c', 65)),
            ("m-1", "p-é", "c-1"),
            ("ID:broker-host-1:1:1:1", "p-1", "c-1"),
        ];

        // Act
        foreach (var (messageId, causationId, conversationId) in contexts)
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId);
            await transport.SendRequestAsync(envelope, CancellationToken.None);
        }

        // Assert
        recorded.Received.Should().HaveCount(contexts.Length).And.AllSatisfy(context => context.Should().BeNull());
    }
}
