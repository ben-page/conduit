using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Microsoft.Extensions.Options;

namespace Conduit.Bridge.SignalR.Tests.Unit;

[TestClass]
public class BridgeTypeFilterInterceptorTests
{
    private static BridgeTypeFilterInterceptor CreateInterceptor(params string[] allowedTypes)
    {
        var options = new BridgeOptions();
        foreach (var type in allowedTypes)
        {
            options.AllowedTypes.Add(type);
        }

        return new BridgeTypeFilterInterceptor(Options.Create(options));
    }

    [TestMethod]
    public async Task InterceptRequestAsync_AllowedType_CallsNext()
    {
        var interceptor = CreateInterceptor("CreateOrder");
        var envelope = new DispatchEnvelope("CreateOrder", JsonSerializer.SerializeToElement(new { }));
        var nextCalled = false;

        var result = await interceptor.InterceptRequestAsync(
            envelope,
            () =>
            {
                nextCalled = true;
                return Task.FromResult<object?>("ok");
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
    }

    [TestMethod]
    public async Task InterceptRequestAsync_DisallowedType_ThrowsUnauthorized()
    {
        var interceptor = CreateInterceptor("CreateOrder");
        var envelope = new DispatchEnvelope("DeleteDatabase", JsonSerializer.SerializeToElement(new { }));

        var action = () => interceptor.InterceptRequestAsync(
            envelope,
            () => Task.FromResult<object?>("ok"),
            CancellationToken.None);

        await action.Should().ThrowExactlyAsync<Exceptions.UnauthorizedException>()
            .WithMessage("*DeleteDatabase*");
    }

    [TestMethod]
    public async Task InterceptEventAsync_AlwaysCallsNext()
    {
        var interceptor = CreateInterceptor(); // no types allowed
        var envelope = new DispatchEnvelope("SomeEvent", JsonSerializer.SerializeToElement(new { }));
        var nextCalled = false;

        await interceptor.InterceptEventAsync(
            envelope,
            () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
    }
}
