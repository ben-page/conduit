using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class TransportDispatcherShould
{
    [TestMethod]
    public async Task SendARoutedFireAndForgetCommandWithoutAwaitingAReply()
    {
        // Arrange -- the outbox relay and flush hand TransportDispatcher a captured envelope, with the generated
        // classifier resolved from their own scope
        var remote = new SpyTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit => conduit.AddEndpoint("remote", endpoint =>
        {
            endpoint.UseTransport(remote);
            endpoint.RouteType<ImportDataCommand>();
            endpoint.RouteType<DeleteItemCommand>();
        }));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var localDispatcher = scope.ServiceProvider.GetRequiredService<ILocalEnvelopeDispatcher>();
        var dispatcher = provider.GetRequiredService<TransportDispatcher>();
        var fireAndForget = Envelope(new ImportDataCommand("orders.csv"));
        var confirmed = Envelope(new DeleteItemCommand(Guid.NewGuid()));

        // Act
        await dispatcher.SendAsync(fireAndForget, localDispatcher, CancellationToken.None);
        await dispatcher.SendAsync(confirmed, localDispatcher, CancellationToken.None);

        // Assert -- the [FireAndForget] command goes out as the publish-time path sends it, with no reply awaited;
        // a plain void command is still a request, whose reply confirms its handler ran
        remote.SentEvents.Select(envelope => envelope.Type).Should().Equal(typeof(ImportDataCommand).FullName);
        remote.SentRequests.Select(envelope => envelope.Type).Should().Equal(typeof(DeleteItemCommand).FullName);
    }

    [TestMethod]
    public async Task BroadcastAnEventToEveryTransportInsteadOfRoutingIt()
    {
        // Arrange -- a route covers the event's namespace, but events never route: a relayed event broadcasts as
        // PublishAsync broadcasts it
        var alpha = new SpyTransport();
        var beta = new SpyTransport();
        await using var provider = BuildServicesRoutingTheSupportNamespace(alpha, beta);
        await using var scope = provider.CreateAsyncScope();
        var localDispatcher = scope.ServiceProvider.GetRequiredService<ILocalEnvelopeDispatcher>();
        var dispatcher = provider.GetRequiredService<TransportDispatcher>();

        // Act
        await dispatcher.SendAsync(Envelope(new ItemCreatedEvent(Guid.NewGuid(), "widget")), localDispatcher, CancellationToken.None);

        // Assert
        alpha.SentEvents.Select(envelope => envelope.Type).Should().Equal(typeof(ItemCreatedEvent).FullName);
        beta.SentEvents.Select(envelope => envelope.Type).Should().Equal(typeof(ItemCreatedEvent).FullName);
        alpha.SentRequests.Should().BeEmpty();
        beta.SentRequests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotBroadcastALocalOnlyEvent()
    {
        // Arrange
        var alpha = new SpyTransport();
        var beta = new SpyTransport();
        await using var provider = BuildServicesRoutingTheSupportNamespace(alpha, beta);
        await using var scope = provider.CreateAsyncScope();
        var localDispatcher = scope.ServiceProvider.GetRequiredService<ILocalEnvelopeDispatcher>();
        var dispatcher = provider.GetRequiredService<TransportDispatcher>();

        // Act
        await dispatcher.SendAsync(Envelope(new LocalOnlyEvent("in process")), localDispatcher, CancellationToken.None);

        // Assert -- neither broadcast nor routed
        alpha.SentEvents.Should().BeEmpty();
        beta.SentEvents.Should().BeEmpty();
        alpha.SentRequests.Should().BeEmpty();
        beta.SentRequests.Should().BeEmpty();
    }

    // Two broadcast transports; the first is also the "remote" endpoint's, as a broker transport's keyed instance has a
    // non-keyed forwarder, and that endpoint routes Conduit.Tests.Support -- the namespace of the support events.
    private static ServiceProvider BuildServicesRoutingTheSupportNamespace(SpyTransport alpha, SpyTransport beta)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit => conduit.AddEndpoint("remote", endpoint =>
        {
            endpoint.UseTransport(alpha);
            endpoint.RouteNamespace<ItemCreatedEvent>();
        }));
        services.AddSingleton<IConduitTransport>(alpha);
        services.AddSingleton<IConduitTransport>(beta);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static DispatchEnvelope Envelope<TMessage>(TMessage message)
        => new(typeof(TMessage).FullName!, JsonSerializer.SerializeToElement(message, ConduitRuntime.JsonOptions));
}
