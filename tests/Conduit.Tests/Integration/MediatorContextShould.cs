using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public sealed class MediatorContextShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task RestoreTheCallerContextWhenAuditSerializationThrows()
    {
        // Arrange -- an attached observer makes the mediator capture the command's body, which throws
        await using var host = await StartHostAsync(new ObservationCollector());
        var mediator = host.GetRequiredService<IMediator>();
        var caller = MessageContext.StartDispatch();

        // Act
        var dispatch = () => mediator.ExecuteAsync(new UnauditableCommand("unreadable"));

        // Assert
        await dispatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("The audit trail cannot read 'unreadable'.");
        MessageContext.Current.Should().BeSameAs(caller);
    }

    [TestMethod]
    public async Task ReportNoFailureForADispatchNoObserverSawStart()
    {
        // Arrange -- the capture runs before DispatchInvoked, so its failure is not reported as a DispatchFailed:
        // observers key on DispatchInvoked coming first
        var collector = new ObservationCollector();
        await using var host = await StartHostAsync(collector);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var dispatch = () => mediator.ExecuteAsync(new UnauditableCommand("unreadable"));

        // Assert
        await dispatch.Should().ThrowAsync<InvalidOperationException>();
        collector.Collected.Should().BeEmpty();
    }

    private static Task<TestHost> StartHostAsync(IConduitObserver observer) =>
        TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton(observer);
            builder.Services.AddConduit();
        });
}
