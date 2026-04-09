using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Tests query routing over Azure Service Bus: correct endpoint delivery, response data,
/// error propagation, and timeout behavior.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AsbCommandRoutingShould"/> but exercises the query path
/// (FetchAsync) instead of commands. Assertions enforce strict unicast semantics:
/// exactly one handler invocation and zero bystander invocations.
/// </remarks>
[TestClass]
public class AsbQueryRoutingShould : AsbTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AzureServiceBus.Tests.Support.Contracts";

    [TestMethod]
    public async Task RouteQueryToCorrectEndpoint()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var bystanderName = UniqueAppName("bystander");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var bystanderHost = await CreateHostAsync(bystanderName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new TestQuery("hello"));

        // Assert — query was routed to exactly one handler, bystander gets nothing
        result.Value.Should().Be("result:hello");
        await handlerSpy.WaitForCountAsync(1);

        // Allow brief settling time to catch any delayed spurious deliveries
        await Task.Delay(2000);

        handlerSpy.Invocations.OfType<TestQuery>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestQuery>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task ReturnQueryResponseWithCorrectData()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        await using var senderHost = await CreateSenderHostAsync(senderName, handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new TestQuery("round-trip"));

        // Assert
        result.Value.Should().Be("result:round-trip");
    }

    [TestMethod]
    public async Task PropagateConduitExceptionFromQuery()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        await using var senderHost = await CreateSenderHostAsync(senderName, handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new ThrowingQuery("missing"));

        // Assert — ConduitException (NotFound) propagated as InvalidOperationException with error code
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*NotFound*");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task TimeoutQueryWhenNoHandler()
    {
        // Arrange
        var senderName = UniqueAppName("sender");
        var prefix = new ConduitOptions { TopologyMode = TopologyMode.Test }.TopologyPrefix!;

        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(5));

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new TestQuery("nobody-home"));

        // Assert — the hardcoded timeout in ASB transport causes TaskCanceledException
        await action.Should().ThrowAsync<Exception>();
    }
}
