using AwesomeAssertions;
using Conduit.Jobs.Runtime;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class InMemoryJobStateShould
{
    [TestMethod]
    public void ReportARunWhoseCancellationSourceIsAlreadyDisposedAsNotRunning()
    {
        // Arrange — the run finished and disposed its source between CancelAsync's lookup and its cancel
        var state = new InMemoryJobState();
        var cancellation = new CancellationTokenSource();
        state.TryTrack("warm", "warm@manual-1", cancellation).Should().BeTrue();
        cancellation.Dispose();

        // Act
        var act = () => state.TryCancel("warm");

        // Assert — the same answer as a run that has finished, not an exception out of IJobs.CancelAsync
        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [TestMethod]
    public void LetOnlyOneRunOfAScheduleRegisterAtATime()
    {
        // Arrange
        var state = new InMemoryJobState();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        state.TryTrack("warm", "warm@manual-1", first).Should().BeTrue();

        // Act
        var secondRegistered = state.TryTrack("warm", "warm@manual-2", second);
        state.Forget("warm", second);

        // Assert — the refused run cannot forget the registered one's registration
        secondRegistered.Should().BeFalse();
        state.TryCancel("warm").Should().BeTrue();
        first.IsCancellationRequested.Should().BeTrue();
    }
}
