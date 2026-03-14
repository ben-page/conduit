using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Transport.SignalR.Tests.Unit;

[TestClass]
public class SignalRTransportTests
{
    [TestMethod]
    public void DispatchEnvelope_RoundTrips()
    {
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        envelope.Type.Should().Be("Test");
    }
}
