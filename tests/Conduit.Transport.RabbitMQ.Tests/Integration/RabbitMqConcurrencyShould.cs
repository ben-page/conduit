using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests that a consumer's concurrency is real: up to that many of its handlers run at once, never more.
/// </summary>
[TestClass]
public class RabbitMqConcurrencyShould : RabbitMqTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.RabbitMQ.Tests.Support.Contracts";

    [TestMethod]
    [Timeout(60_000)]
    public async Task RunHandlersInParallelUpToConcurrency()
    {
        // Arrange — twice as many commands as the consumer's concurrency, each handler long enough to overlap
        const int concurrency = 4;
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(
            handlerName,
            configureOptions: options => options.DefaultConcurrency = concurrency);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, concurrency * 2).Select(index =>
            mediator.ExecuteAsync(new ConcurrencyProbeCommand($"probe-{index}", DelayMs: 1000))));

        // Assert — every command was handled, and exactly that many handlers ran at once: in parallel,
        // and never beyond the consumer's concurrency
        results.Select(result => result.Value).Should().OnlyContain(value => value.StartsWith("probed:", StringComparison.Ordinal));
        var marks = handlerSpy.Invocations.OfType<ConcurrencyProbeMark>().ToList();
        marks.Should().HaveCount(concurrency * 4);
        ConcurrencyProbeMark.MaxRunningAtOnce(marks).Should().Be(concurrency);
    }
}
