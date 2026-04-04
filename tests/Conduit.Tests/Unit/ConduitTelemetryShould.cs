using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class ConduitTelemetryShould : IDisposable
{
    private ActivityListener? listener;
    private readonly List<Activity> capturedActivities = [];

    [TestInitialize]
    public void Setup()
    {
        this.listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = SampleAllData,
            ActivityStarted = this.capturedActivities.Add,
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
        this.capturedActivities.Clear();
    }

    public void Dispose()
    {
        this.listener?.Dispose();
    }

    [TestMethod]
    public void CreateActivityWithCallerTypeAndMethodName()
    {
        // Act
        using var activity = ConduitTelemetry.StartActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething", "command");

        // Assert
        activity.Should().NotBeNull();
        activity!.OperationName.Should().Contain("ConduitTelemetryShould");
        activity.OperationName.Should().Contain("CreateActivityWithCallerTypeAndMethodName");
    }

    [TestMethod]
    public void SetStandardTagsOnActivity()
    {
        // Arrange
        MessageContext.StartDispatch();

        // Act
        using var activity = ConduitTelemetry.StartActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething", "command");

        // Assert
        activity.Should().NotBeNull();
        activity!.GetTagItem("conduit.message.type").Should().Be("Test.Commands.DoSomething");
        activity.GetTagItem("conduit.message.kind").Should().Be("command");
        activity.GetTagItem("conduit.message.id").Should().NotBeNull();
        activity.GetTagItem("conduit.conversation.id").Should().NotBeNull();

        // Cleanup
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ReturnNullWhenNoListenerAttached()
    {
        // Arrange — dispose the listener so nothing is subscribed
        this.listener?.Dispose();
        this.listener = null;

        // Act
        using var activity = ConduitTelemetry.StartActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething", "command");

        // Assert
        activity.Should().BeNull();
    }

    [TestMethod]
    public void UseProducerKindForTransportSend()
    {
        // Act
        using var activity = ConduitTelemetry.StartProducerActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething");

        // Assert
        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Producer);
    }

    [TestMethod]
    public void UseConsumerKindForTransportReceive()
    {
        // Act
        using var activity = ConduitTelemetry.StartConsumerActivity<ConduitTelemetryShould>(
            "Test.Events.SomethingHappened", parentContext: null);

        // Assert
        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Consumer);
    }

    [TestMethod]
    public void UseInternalKindByDefault()
    {
        // Act
        using var activity = ConduitTelemetry.StartActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething", "command");

        // Assert
        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Internal);
    }

    [TestMethod]
    public void PropagateTraceContextThroughCarrier()
    {
        // Arrange — start a producer activity so there is an active context
        using var producerActivity = ConduitTelemetry.StartProducerActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething");
        producerActivity.Should().NotBeNull();

        var headers = new Dictionary<string, string>();

        // Act — inject trace context into carrier
        ConduitTelemetry.InjectTraceContext(producerActivity, (key, value) => headers[key] = value);

        // Assert — traceparent should be present
        headers.Should().ContainKey("traceparent");
        headers["traceparent"].Should().StartWith("00-");

        // Act — extract trace context from carrier
        var extractedContext = ConduitTelemetry.ExtractTraceContext(key =>
            headers.TryGetValue(key, out var value) ? value : null);

        // Assert — round-trip preserves trace ID
        extractedContext.Should().NotBeNull();
        extractedContext!.Value.TraceId.Should().Be(producerActivity!.TraceId);
    }

    [TestMethod]
    public void LinkConsumerActivityToProducerActivity()
    {
        // Arrange — create a producer activity and extract its context
        using var producerActivity = ConduitTelemetry.StartProducerActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething");
        producerActivity.Should().NotBeNull();

        var headers = new Dictionary<string, string>();
        ConduitTelemetry.InjectTraceContext(producerActivity, (key, value) => headers[key] = value);

        var parentContext = ConduitTelemetry.ExtractTraceContext(key =>
            headers.TryGetValue(key, out var value) ? value : null);

        // Act — create a consumer activity linked to the producer
        using var consumerActivity = ConduitTelemetry.StartConsumerActivity<ConduitTelemetryShould>(
            "Test.Commands.DoSomething", parentContext);

        // Assert — consumer shares the same trace ID
        consumerActivity.Should().NotBeNull();
        consumerActivity!.TraceId.Should().Be(producerActivity!.TraceId);
        consumerActivity.ParentSpanId.Should().Be(producerActivity.SpanId);
    }
}
