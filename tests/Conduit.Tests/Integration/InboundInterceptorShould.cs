using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class InboundInterceptorShould
{
    private static TestWebHost CreateTestHost(Action<IServiceCollection>? configure = null)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                configure?.Invoke(builder.Services);
            });
    }

    [TestMethod]
    public async Task RejectMessageWhenGateReturnsFalse()
    {
        // Arrange
        var interceptor = new RejectAllInterceptor();
        await using var host = CreateTestHost(services =>
            services.AddSingleton<IInboundInterceptor>(interceptor));
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Should Be Rejected" }));

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert — gate rejected, so dispatch did not happen
        result.Data.Should().BeNull();
    }

    [TestMethod]
    public async Task AllowMessageWhenGateReturnsTrue()
    {
        // Arrange
        var interceptor = new AllowAllInterceptor();
        await using var host = CreateTestHost(services =>
            services.AddSingleton<IInboundInterceptor>(interceptor));
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

    [TestMethod]
    public async Task RejectEventWhenGateReturnsFalse()
    {
        // Arrange
        var interceptor = new RejectAllInterceptor();
        await using var host = CreateTestHost(services =>
            services.AddSingleton<IInboundInterceptor>(interceptor));
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.ItemCreatedEvent",
            JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Rejected Event" }));

        // Act
        await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert — event should NOT reach the listener
        await Task.Delay(100);
        spy.Events.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ChainMultipleGatesFirstRejectionWins()
    {
        // Arrange
        var allow = new AllowAllInterceptor();
        var reject = new RejectAllInterceptor();
        await using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IInboundInterceptor>(allow);
            services.AddSingleton<IInboundInterceptor>(reject);
        });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Tests.Support.CreateItemCommand",
            JsonSerializer.SerializeToElement(new { Name = "Chain Test" }));

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert — second gate rejects
        result.Data.Should().BeNull();
    }

    [TestMethod]
    public async Task RejectUnauthorizedTypeViaTypeFilter()
    {
        // Arrange
        await using var host = TestWebHost.Start(
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

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("Unauthorized");
    }

    [TestMethod]
    public async Task AllowAuthorizedTypeViaTypeFilter()
    {
        // Arrange
        await using var host = TestWebHost.Start(
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

file class RejectAllInterceptor : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }
}

file class AllowAllInterceptor : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}
