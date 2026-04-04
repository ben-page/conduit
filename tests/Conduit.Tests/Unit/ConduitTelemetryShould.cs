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
}
