using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class EventBroadcastShould
{
    [TestMethod]
    public async Task SendEventToTransportOnPublish()
    {
        // Arrange
        var recordingTransport = new RecordingTransport();

        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddSingleton<IConduitTransport>(recordingTransport);
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("recorder", endpoint =>
                    {
                        endpoint.UseTransport(recordingTransport);
                        endpoint.RouteNamespace<ItemCreatedEvent>();
                    });
                });
            });

        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Broadcast Test"));

        // Assert
        await spy.WaitForCountAsync(1);
        recordingTransport.SentEvents.Should().HaveCount(1);
        recordingTransport.SentEvents[0].Type.Should().Contain("ItemCreatedEvent");
    }

    [TestMethod]
    public async Task NotReBroadcastWireInboundEvent()
    {
        // Arrange
        var recordingTransport = new RecordingTransport();

        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddSingleton<IConduitTransport>(recordingTransport);
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("recorder", endpoint =>
                    {
                        endpoint.UseTransport(recordingTransport);
                        endpoint.RouteNamespace<ItemCreatedEvent>();
                    });
                });
            });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var envelope = new DispatchEnvelope(
            "Conduit.Transport.SignalR.Tests.Support.ItemCreatedEvent",
            System.Text.Json.JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Wire Event" }));

        // Act
        await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert
        await spy.WaitForCountAsync(1);
        recordingTransport.SentEvents.Should().BeEmpty();
    }

    // A server-mode hub used to push every event that is not [LocalOnly] to every connection, signed in or not: in a
    // multi-tenant host every tenant's domain events reached every browser. [ClientVisible] is what lets one reach them.
    [TestMethod]
    public async Task PushOnlyClientVisibleEventsByDefault()
    {
        // Arrange
        await using var host = StartHubHost();
        await using var connection = BuildConnection(host.Address, user: null);
        var received = ReceivedEvents.Listen(connection, typeof(ItemArchivedEvent), typeof(ItemRenamedEvent));
        await StartRegisteredAsync(connection);
        var mediator = host.GetRequiredService<IMediator>();

        // Act: the hidden event first, so it would have arrived before the visible one had it been pushed
        await mediator.PublishAsync(new ItemArchivedEvent(Guid.NewGuid()));
        await mediator.PublishAsync(new ItemRenamedEvent(Guid.NewGuid(), "Renamed"));
        await received.WaitForAsync(typeof(ItemRenamedEvent));

        // Assert
        received.Types.Should().Equal(typeof(ItemRenamedEvent).FullName);
    }

    // A registered resolver decides for every event, from its typed payload: users, groups, or everyone, an event that
    // is not [ClientVisible] included.
    [TestMethod]
    public async Task AskTheResolverWhenOneIsRegistered()
    {
        // Arrange
        var resolver = new ItemRecipientResolver();
        await using var host = StartHubHost(services =>
        {
            services.AddSingleton<IUserIdProvider, QueryStringUserIdProvider>();
            services.AddSingleton<IEventRecipientResolver>(resolver);
        });
        Type[] itemEvents = [typeof(ItemAssignedEvent), typeof(ItemSharedEvent), typeof(ItemArchivedEvent)];
        await using var alice = BuildConnection(host.Address, user: "alice");
        await using var bob = BuildConnection(host.Address, user: "bob");
        var aliceReceived = ReceivedEvents.Listen(alice, itemEvents);
        var bobReceived = ReceivedEvents.Listen(bob, itemEvents);
        await StartRegisteredAsync(alice);
        await StartRegisteredAsync(bob);
        await host.GetRequiredService<IHubContext<ConduitHub>>().Groups.AddToGroupAsync(bob.ConnectionId!, "ops");
        var mediator = host.GetRequiredService<IMediator>();
        var assigned = new ItemAssignedEvent(Guid.NewGuid(), "alice");

        // Act: the archive last, to everyone, so each connection has had everything meant for it once it arrives
        await mediator.PublishAsync(assigned);
        await mediator.PublishAsync(new ItemSharedEvent(Guid.NewGuid(), "ops"));
        await mediator.PublishAsync(new ItemArchivedEvent(Guid.NewGuid()));
        await aliceReceived.WaitForAsync(typeof(ItemArchivedEvent));
        await bobReceived.WaitForAsync(typeof(ItemArchivedEvent));

        // Assert
        aliceReceived.Types.Should().Equal(typeof(ItemAssignedEvent).FullName, typeof(ItemArchivedEvent).FullName);
        bobReceived.Types.Should().Equal(typeof(ItemSharedEvent).FullName, typeof(ItemArchivedEvent).FullName);
        resolver.Asked.Select(asked => asked.EventType).Should().Equal(itemEvents.Select(type => type.FullName));
        resolver.Asked.First().Event.Should().Be(assigned);
    }

    // Under the outbox an event reaches the transports through TransportDispatcher, which does not isolate one transport
    // from the next: a resolver fault escaping the transport aborted the broadcast, failed the outbox entry and resent it
    // to the transports that had already taken it. The transport logs the fault and pushes nothing for that event instead.
    [TestMethod]
    public async Task LogAndSkipTheEventWhenTheResolverThrows()
    {
        // Arrange
        var log = new SpyLoggerProvider();
        var laterTransport = new RecordingTransport();
        await using var host = StartHubHost(services =>
        {
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton<IEventRecipientResolver, FaultyRecipientResolver>();
            services.AddSingleton<IConduitTransport>(laterTransport);
        });
        await using var connection = BuildConnection(host.Address, user: null);
        var received = ReceivedEvents.Listen(connection, typeof(ItemAssignedEvent), typeof(ItemRenamedEvent));
        await StartRegisteredAsync(connection);
        var transportDispatcher = host.GetRequiredService<TransportDispatcher>();
        var localDispatcher = host.GetRequiredService<ILocalEnvelopeDispatcher>();

        // Act: delivered as the outbox delivers, the faulted event first
        await transportDispatcher.SendAsync(
            EnvelopeOf(new ItemAssignedEvent(Guid.NewGuid(), "alice")), localDispatcher, CancellationToken.None);
        await transportDispatcher.SendAsync(
            EnvelopeOf(new ItemRenamedEvent(Guid.NewGuid(), "Renamed")), localDispatcher, CancellationToken.None);
        await received.WaitForAsync(typeof(ItemRenamedEvent));

        // Assert
        received.Types.Should().Equal(typeof(ItemRenamedEvent).FullName);
        laterTransport.SentEvents.Select(envelope => envelope.Type).Should()
            .Equal(typeof(ItemAssignedEvent).FullName, typeof(ItemRenamedEvent).FullName);
        log.Entries.Should().ContainSingle(entry =>
                entry.Category == typeof(SignalRTransport).FullName
                && entry.Level == LogLevel.Warning
                && entry.EventId.Id == 100)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static DispatchEnvelope EnvelopeOf(object @event)
    {
        return new DispatchEnvelope(
            @event.GetType().FullName!,
            JsonSerializer.SerializeToElement(@event, @event.GetType(), ConduitRuntime.JsonOptions));
    }

    private static TestWebHost StartHubHost(Action<IServiceCollection>? configureServices = null)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR(new ConduitServerInfo("event-broadcast"));
                configureServices?.Invoke(builder.Services);
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static HubConnection BuildConnection(string address, string? user)
    {
        var query = user is null ? string.Empty : $"?user={user}";
        return new HubConnectionBuilder().WithUrl($"{address}/conduit{query}").Build();
    }

    // StartAsync returns at the handshake, before the server registers the connection for Clients.All, users and groups.
    // The hub greets a connection with its ConduitServerInfo only after registering it, so the greeting is the signal.
    private static async Task StartRegisteredAsync(HubConnection connection)
    {
        var greeted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var greeting = connection.On<ConduitServerInfo>(ConduitHub.ServerInfoMethod, _ => greeted.TrySetResult());
        await connection.StartAsync();
        await greeted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

file class RecordingTransport : IConduitTransport
{
    public List<DispatchEnvelope> SentEvents { get; } = [];

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentEvents.Add(envelope);
        return Task.CompletedTask;
    }

    public Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
