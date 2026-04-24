using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public sealed class TracingIntegrationShould : IDisposable
{
    private ActivityListener? listener;
    private readonly List<Activity> capturedActivities = [];

    // Root activity started in TestInitialize so every dispatch activity
    // (a child) inherits its TraceId. Without this, other tests running
    // concurrently under [Parallelize(MethodLevel)] leak their dispatch
    // activities into this test's capture list via the global listener.
    private Activity? testRoot;

    [TestInitialize]
    public void Setup()
    {
        this.testRoot = new Activity("TracingIntegrationShould.Scope").Start();

        var rootTraceId = this.testRoot.TraceId;
        this.listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = SampleAllData,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == rootTraceId)
                {
                    this.capturedActivities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(this.listener);
    }

    private static ActivitySamplingResult SampleAllData(
        ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded;

    [TestCleanup]
    public void Cleanup()
    {
        this.listener?.Dispose();
        this.listener = null;
        this.testRoot?.Dispose();
        this.testRoot = null;
        this.capturedActivities.Clear();
    }

    public void Dispose()
    {
        this.listener?.Dispose();
        this.testRoot?.Dispose();
    }

    [TestMethod]
    public async Task TraceLocalCommandDispatch()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new CreateItemCommand("Traced Item"));

        // Assert
        var dispatchActivity = this.capturedActivities
            .FirstOrDefault(a => a.OperationName.Contains("ConduitMediator", StringComparison.Ordinal)
                && a.GetTagItem("conduit.message.kind")?.ToString() == "command");
        dispatchActivity.Should().NotBeNull();
        dispatchActivity!.GetTagItem("conduit.message.type").Should().NotBeNull();
        dispatchActivity.GetTagItem("conduit.dispatch").Should().Be("local");
    }

    [TestMethod]
    public async Task TraceLocalQueryDispatch()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "Query Trace Test");

        // Act
        await mediator.FetchAsync(new GetItemQuery(id));

        // Assert
        var dispatchActivity = this.capturedActivities
            .FirstOrDefault(a => a.OperationName.Contains("ConduitMediator", StringComparison.Ordinal)
                && a.GetTagItem("conduit.message.kind")?.ToString() == "query");
        dispatchActivity.Should().NotBeNull();
        dispatchActivity!.GetTagItem("conduit.dispatch").Should().Be("local");
    }

    [TestMethod]
    public async Task TraceLocalEventPublish()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Traced Event"));

        // Assert
        var publishActivity = this.capturedActivities
            .FirstOrDefault(a => a.OperationName.Contains("ConduitMediator", StringComparison.Ordinal)
                && a.GetTagItem("conduit.message.kind")?.ToString() == "event");
        publishActivity.Should().NotBeNull();
    }
}
