using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Middleware;
using Microsoft.Extensions.Options;

namespace Conduit.Tests.Unit;

[TestClass]
public class TypeFilterInterceptorShould
{
    private static TypeFilterInterceptor CreateInterceptor(params string[] allowedTypes)
    {
        var options = new TypeFilterOptions();
        foreach (var type in allowedTypes)
        {
            options.AllowedTypes.Add(type);
        }

        return new TypeFilterInterceptor(Options.Create(options));
    }

    [TestMethod]
    public async Task AllowPermittedType()
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
    public async Task RejectForbiddenType()
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
    public async Task AlwaysPassEvents()
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
