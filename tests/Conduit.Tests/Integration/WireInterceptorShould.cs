using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class WireInterceptorShould
{
    private static TestHost CreateTestHost(Action<IServiceCollection>? configure = null)
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                configure?.Invoke(builder.Services);
            });
    }

    [TestMethod]
    public async Task InvokeInterceptorOnWireRequest()
    {
        // Arrange
        var interceptor = new StubInterceptor();
        await using var host = CreateTestHost(services =>
            services.AddSingleton<IWireInterceptor>(interceptor));
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Wire Test" }));

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        interceptor.RequestLog.Should().Contain("Conduit.Tests.Support.CreateItemCommand");
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    [TestMethod]
    public async Task InvokeInterceptorOnWireEvent()
    {
        // Arrange
        var interceptor = new StubInterceptor();
        await using var host = CreateTestHost(services =>
            services.AddSingleton<IWireInterceptor>(interceptor));
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.ItemCreatedEvent",
            JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Wire Event" }));

        // Act
        await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert
        interceptor.EventLog.Should().Contain("Conduit.Tests.Support.ItemCreatedEvent");
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task ChainMultipleInterceptorsInRegistrationOrder()
    {
        // Arrange
        var callOrder = new List<string>();
        var first = new OrderTrackingInterceptor("first", callOrder);
        var second = new OrderTrackingInterceptor("second", callOrder);
        await using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IWireInterceptor>(first);
            services.AddSingleton<IWireInterceptor>(second);
        });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Chain Test" }));

        // Act
        await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert — first registered interceptor is outermost (runs first)
        callOrder.Should().HaveCount(2);
        callOrder[0].Should().Be("first");
        callOrder[1].Should().Be("second");
    }

    [TestMethod]
    public async Task RejectUnauthorizedTypeViaTypeFilter()
    {
        // Arrange
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                    conduit.AddTypeFilter(filter =>
                        filter.AllowType("Conduit.Tests.Support.GetItemQuery")));
            });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Blocked" }));

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert — type filter rejects the unauthorized type
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("Unauthorized");
    }

    [TestMethod]
    public async Task AllowAuthorizedTypeViaTypeFilter()
    {
        // Arrange
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                    conduit.AddTypeFilter(filter =>
                        filter.AllowType("Conduit.Tests.Support.CreateItemCommand")));
            });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Allowed" }));

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }
}

file class OrderTrackingInterceptor(string name, List<string> callOrder) : IWireInterceptor
{
    public async Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken)
    {
        callOrder.Add(name);
        return await next();
    }

    public async Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken)
    {
        callOrder.Add(name);
        await next();
    }
}
