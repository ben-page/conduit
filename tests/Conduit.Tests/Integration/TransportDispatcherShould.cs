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

    private static DispatchEnvelope Envelope<TMessage>(TMessage message)
        => new(typeof(TMessage).FullName!, JsonSerializer.SerializeToElement(message, ConduitRuntime.JsonOptions));
}
