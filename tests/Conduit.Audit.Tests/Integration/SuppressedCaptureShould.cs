using System.Text.Json;
using AwesomeAssertions;
using Conduit.Audit.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// A type the host's audit registration suppresses is never serialized for capture -- in process or over the wire --
/// so a type whose serialization throws cannot fail its dispatch through capture.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SuppressedCaptureShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MessageIdCapture>();
        services.AddConduit(conduit => conduit.AddAudit(audit => audit.SuppressPayload<UnserializableCommand>()));
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task DispatchASuppressedTypeInProcessWithoutSerializingIt()
    {
        // Arrange
        await using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Act
        var act = () => mediator.ExecuteAsync(new UnserializableCommand());

        // Assert
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task DispatchASuppressedTypeOverTheWireWithoutSerializingIt()
    {
        // Arrange
        await using var provider = BuildProvider();
        var wireMediator = provider.GetRequiredService<IWireMediator>();
        MessageContext.StartDispatchFromTransport(Guid.NewGuid().ToString(), null, Guid.NewGuid().ToString());

        // Act
        var result = await wireMediator.HandleWireRequestAsync(
            new DispatchEnvelope(typeof(UnserializableCommand).FullName!, JsonSerializer.SerializeToElement(new { })),
            CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
    }
}
