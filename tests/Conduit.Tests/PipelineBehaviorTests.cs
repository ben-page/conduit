using AwesomeAssertions;

namespace Conduit.Tests;

[TestClass]
public class PipelineBehaviorTests
{
    private record TestCommand(string Value) : ICommand<string>;

    private class TrackingBehavior : IPipelineBehavior<TestCommand, string>
    {
        public List<string> CallLog { get; } = [];

        public async Task<string> HandleAsync(
            TestCommand request,
            Func<Task<string>> next,
            CancellationToken cancellationToken)
        {
            this.CallLog.Add("before");
            var result = await next();
            this.CallLog.Add("after");
            return result;
        }
    }

    [TestMethod]
    public async Task PipelineBehavior_WrapsHandler()
    {
        var behavior = new TrackingBehavior();
        var result = await behavior.HandleAsync(
            new TestCommand("test"),
            () => Task.FromResult("handled"),
            CancellationToken.None);

        result.Should().Be("handled");
        behavior.CallLog.Should().BeEquivalentTo(["before", "after"]);
    }

    private record TestEvent(string Data) : IEvent;

    private class TrackingEventBehavior : IEventPipelineBehavior<TestEvent>
    {
        public bool WasCalled { get; private set; }

        public async Task HandleAsync(
            TestEvent @event,
            Func<Task> next,
            CancellationToken cancellationToken)
        {
            this.WasCalled = true;
            await next();
        }
    }

    [TestMethod]
    public async Task EventPipelineBehavior_WrapsListener()
    {
        var behavior = new TrackingEventBehavior();
        await behavior.HandleAsync(
            new TestEvent("test"),
            () => Task.CompletedTask,
            CancellationToken.None);

        behavior.WasCalled.Should().BeTrue();
    }
}
