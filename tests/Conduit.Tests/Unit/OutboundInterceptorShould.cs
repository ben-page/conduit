using AwesomeAssertions;
using Conduit.Middleware;
using Conduit.Runtime;
using System.Text.Json;

namespace Conduit.Tests.Unit;

[TestClass]
public class OutboundInterceptorShould
{
    [TestMethod]
    public void AcceptDispatchEnvelope()
    {
        // Arrange
        var captured = new List<DispatchEnvelope>();
        IOutboundInterceptor interceptor = new TestCapture(captured);
        var envelope = new DispatchEnvelope("Test.Event", JsonSerializer.SerializeToElement(new { }));

        // Act
        interceptor.Intercept(envelope);

        // Assert
        captured.Should().ContainSingle()
            .Which.Type.Should().Be("Test.Event");
    }

    private sealed class TestCapture(List<DispatchEnvelope> captured) : IOutboundInterceptor
    {
        public void Intercept(DispatchEnvelope envelope) => captured.Add(envelope);
    }
}
