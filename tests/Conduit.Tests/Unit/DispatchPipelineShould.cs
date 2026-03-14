using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Exceptions;
using Conduit.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

[TestClass]
public class DispatchPipelineShould
{
    private static DispatchEnvelope MakeEnvelope(string type = "TestType") =>
        new(type, JsonSerializer.SerializeToElement(new { }));

    [TestMethod]
    public async Task ReturnOkForSuccessfulRequest()
    {
        // Arrange
        var router = new StubRouter { RequestResult = "hello" };

        // Act
        var result = await DispatchPipeline.ExecuteRequestAsync(
            MakeEnvelope(), [], router, NullLogger.Instance, CancellationToken.None);

        // Assert
        result.Data.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task ReturnFailForConduitException()
    {
        // Arrange
        var router = new StubRouter { RequestException = new NotFoundException("gone") };

        // Act
        var result = await DispatchPipeline.ExecuteRequestAsync(
            MakeEnvelope(), [], router, NullLogger.Instance, CancellationToken.None);

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NotFound");
        result.Error.Message.Should().Be("gone");
    }

    [TestMethod]
    public async Task ReturnInternalErrorForUnhandledException()
    {
        // Arrange
        var router = new StubRouter { RequestException = new InvalidOperationException("boom") };

        // Act
        var result = await DispatchPipeline.ExecuteRequestAsync(
            MakeEnvelope(), [], router, NullLogger.Instance, CancellationToken.None);

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("InternalError");
    }

    [TestMethod]
    public async Task ChainInterceptorsForRequests()
    {
        // Arrange
        var interceptor1 = new StubInterceptor();
        var interceptor2 = new StubInterceptor();
        var router = new StubRouter { RequestResult = "ok" };

        // Act
        await DispatchPipeline.ExecuteRequestAsync(
            MakeEnvelope("MyCommand"), [interceptor1, interceptor2], router,
            NullLogger.Instance, CancellationToken.None);

        // Assert
        interceptor1.RequestLog.Should().Contain("MyCommand");
        interceptor2.RequestLog.Should().Contain("MyCommand");
    }

    [TestMethod]
    public async Task SwallowExceptionsForEvents()
    {
        // Arrange
        var router = new StubRouter { EventException = new InvalidOperationException("boom") };

        // Act
        var act = () => DispatchPipeline.ExecuteEventAsync(
            MakeEnvelope(), [], router, NullLogger.Instance, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ChainInterceptorsForEvents()
    {
        // Arrange
        var interceptor = new StubInterceptor();
        var router = new StubRouter();

        // Act
        await DispatchPipeline.ExecuteEventAsync(
            MakeEnvelope("MyEvent"), [interceptor], router,
            NullLogger.Instance, CancellationToken.None);

        // Assert
        interceptor.EventLog.Should().Contain("MyEvent");
    }
}
