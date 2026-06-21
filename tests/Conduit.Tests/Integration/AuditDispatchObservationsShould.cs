using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class AuditDispatchObservationsShould
{
    private ObservationCollector collector = null!;

    [TestInitialize]
    public void Setup()
    {
        this.collector = new ObservationCollector();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    /// <summary>
    /// Starts a host with the given observer registered as the per-host <see cref="IConduitObserver"/>.
    /// The generated mediator resolves it from the dispatch scope and attaches it to the context —
    /// there is no process-global observer to set.
    /// </summary>
    private static Task<TestHost> StartHostAsync(IConduitObserver observer) =>
        TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton(observer);
            builder.Services.AddConduit();
        });

    [TestMethod]
    public async Task EmitDispatchInvokedAndCompletedForCommand()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.ExecuteAsync(new EchoCommand("hello"));

        // Assert
        result.Should().Be("hello");

        var invoked = this.collector.OfType<DispatchInvoked>();
        invoked.Should().HaveCount(1);

        var completed = this.collector.OfType<DispatchCompleted>();
        completed.Should().HaveCount(1);
        completed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task EmitDispatchInvokedAndCompletedForVoidCommand()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new SimpleVoidCommand("test"));

        // Assert
        var invoked = this.collector.OfType<DispatchInvoked>();
        invoked.Should().HaveCount(1);

        var completed = this.collector.OfType<DispatchCompleted>();
        completed.Should().HaveCount(1);
        completed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task EmitDispatchInvokedAndCompletedForQuery()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new SimpleQuery("world"));

        // Assert
        result.Should().Be("world");

        var invoked = this.collector.OfType<DispatchInvoked>();
        invoked.Should().HaveCount(1);

        var completed = this.collector.OfType<DispatchCompleted>();
        completed.Should().HaveCount(1);
        completed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task EmitDispatchFailedOnException()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var act = () => mediator.ExecuteAsync(new FailingCommand("test failure"));
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert
        var invoked = this.collector.OfType<DispatchInvoked>();
        invoked.Should().HaveCount(1);

        var failed = this.collector.OfType<DispatchFailed>();
        failed.Should().HaveCount(1);
        failed[0].ErrorMessage.Should().Be("test failure");
        failed[0].ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        failed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);

        this.collector.OfType<DispatchCompleted>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmitDispatchInvokedAndCompletedForEvent()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new SimpleEvent("test data"));

        // Assert
        var invoked = this.collector.OfType<DispatchInvoked>();
        invoked.Should().HaveCount(1);

        var completed = this.collector.OfType<DispatchCompleted>();
        completed.Should().HaveCount(1);
        completed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task EmitHandlerStartedAndCompletedPerListener()
    {
        // Arrange
        await using var host = await StartHostAsync(this.collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new SimpleEvent("listener test"));

        // Assert
        var started = this.collector.OfType<HandlerStarted>();
        started.Should().HaveCount(1);
        started[0].HandlerType.Should().Contain(nameof(SimpleEventListener));

        var completed = this.collector.OfType<HandlerCompleted>();
        completed.Should().HaveCount(1);
        completed[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task CapturePayloadOnDispatch()
    {
        // Arrange — a custom observer registered as the host's IConduitObserver.
        var payloadCollector = new PayloadCapturingObserver();
        await using var host = await StartHostAsync(payloadCollector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("payload-test"));

        // Assert
        payloadCollector.CapturedPayload.Should().NotBeNull();
        payloadCollector.CapturedPayload!.Value.ToString().Should().Contain("payload-test");
    }

    [TestMethod]
    public async Task PassPropertiesViaDispatchOptions()
    {
        // Arrange — a custom observer registered as the host's IConduitObserver.
        var propertiesCollector = new PropertiesCapturingObserver();
        await using var host = await StartHostAsync(propertiesCollector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(
            new EchoCommand("options-test"),
            opts => opts.Tag("key", "value"));

        // Assert
        propertiesCollector.CapturedProperties.Should().NotBeNull();
        propertiesCollector.CapturedProperties.Should().ContainKey("key");
        propertiesCollector.CapturedProperties!["key"].Should().Be("value");
    }

    /// <summary>
    /// Observer that captures the Payload from MessageContext on the first DispatchInvoked observation.
    /// </summary>
    private sealed class PayloadCapturingObserver : IConduitObserver
    {
        public System.Text.Json.JsonElement? CapturedPayload { get; private set; }

        public void OnObservation(MessageObservation observation, MessageContext context)
        {
            if (observation is DispatchInvoked && this.CapturedPayload is null)
            {
                this.CapturedPayload = context.Payload;
            }
        }
    }

    /// <summary>
    /// Observer that captures the Properties from MessageContext on the first DispatchInvoked observation.
    /// </summary>
    private sealed class PropertiesCapturingObserver : IConduitObserver
    {
        public IReadOnlyDictionary<string, object?>? CapturedProperties { get; private set; }

        public void OnObservation(MessageObservation observation, MessageContext context)
        {
            if (observation is DispatchInvoked && this.CapturedProperties is null)
            {
                this.CapturedProperties = context.Properties;
            }
        }
    }
}
