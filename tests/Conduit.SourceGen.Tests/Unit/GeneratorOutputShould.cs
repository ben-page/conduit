using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen.Tests.Unit;

/// <summary>
/// The code the generator emits compiles in the consuming project, for every message shape it accepts -- every emitted
/// file, the REST endpoints and resilience dispatch included (both feature assemblies are referenced) -- and a shape it
/// cannot emit is reported instead of emitted.
/// </summary>
[TestClass]
public class GeneratorOutputShould : GeneratorTestBase
{
    /// <summary>
    /// Every message shape the generator tests exercise, in one compilation: each kind with and without a handler, a
    /// nested type, every event attribute, a type-level [DoNotAudit], a reference-type, nullable and value-type result.
    /// </summary>
    private const string SupportTypesSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Conduit.Attributes;
        using Conduit.Handlers;
        using Conduit.Messages;

        namespace App.Models
        {
            public sealed record OrderDto(Guid Id, string CustomerId);
        }

        namespace App.Commands
        {
            public sealed record CreateOrder(string CustomerId) : ICommand<Guid>;

            public sealed class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
            {
                public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
            }

            public sealed record DeleteOrder(Guid Id) : ICommand;

            public sealed class DeleteOrderHandler : ICommandHandler<DeleteOrder>
            {
                public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [FireAndForget]
            public sealed record RecalculateTotals(Guid OrderId) : ICommand;

            public sealed class RecalculateTotalsHandler : ICommandHandler<RecalculateTotals>
            {
                public Task HandleAsync(RecalculateTotals command, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [DoNotAudit]
            public sealed record StoreSecret(string Secret) : ICommand<int>;

            public sealed class StoreSecretHandler : ICommandHandler<StoreSecret, int>
            {
                public Task<int> HandleAsync(StoreSecret command, CancellationToken cancellationToken) => Task.FromResult(1);
            }

            // Routed elsewhere: no local handler (CONDUIT001), so its overloads are route-only.
            public sealed record ShipOrder(Guid Id) : ICommand<bool>;

            public sealed record ArchiveOrder(Guid Id) : ICommand;

            public static class Billing
            {
                public sealed record ChargeCard(string Token) : ICommand<string>;

                public sealed class ChargeCardHandler : ICommandHandler<ChargeCard, string>
                {
                    public Task<string> HandleAsync(ChargeCard command, CancellationToken cancellationToken) => Task.FromResult("charged");
                }
            }
        }

        namespace App.Queries
        {
            public sealed record GetOrder(Guid Id) : IQuery<App.Models.OrderDto?>;

            public sealed class GetOrderHandler : IQueryHandler<GetOrder, App.Models.OrderDto?>
            {
                public Task<App.Models.OrderDto?> HandleAsync(GetOrder query, CancellationToken cancellationToken)
                    => Task.FromResult<App.Models.OrderDto?>(null);
            }

            public sealed record ListOrders(int Page) : IQuery<IReadOnlyList<App.Models.OrderDto>>;

            public sealed class ListOrdersHandler : IQueryHandler<ListOrders, IReadOnlyList<App.Models.OrderDto>>
            {
                public Task<IReadOnlyList<App.Models.OrderDto>> HandleAsync(ListOrders query, CancellationToken cancellationToken)
                    => Task.FromResult<IReadOnlyList<App.Models.OrderDto>>([]);
            }

            public sealed record CountOrders : IQuery<int>;
        }

        namespace App.Events
        {
            public sealed record OrderCreated(Guid Id) : IEvent;

            public sealed class OrderCreatedListener : IEventListener<OrderCreated>
            {
                public Task HandleAsync(OrderCreated @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class OrderCreatedAuditor : IEventListener<OrderCreated>
            {
                public Task HandleAsync(OrderCreated @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [LocalOnly]
            public sealed record CacheInvalidated(string Key) : IEvent;

            [FireAndForget]
            public sealed record OrderShipped(Guid Id) : IEvent;

            public sealed class OrderShippedListener : IEventListener<OrderShipped>
            {
                public Task HandleAsync(OrderShipped @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [FireAndForget(Immediate = true)]
            public sealed record OrderViewed(Guid Id) : IEvent;

            // No listener at compile time: one may be registered at runtime.
            public sealed record OrderArchived(Guid Id) : IEvent;

            public static class Outer
            {
                [FireAndForget]
                public sealed record NestedEvent(string Detail) : IEvent;
            }
        }
        """;

    [TestMethod]
    public void CompileForTheSupportTypes()
    {
        // Act
        var (output, _) = RunEveryEmitter(SupportTypesSource);

        // Assert -- RunGenerator asserted the compilation; every file the generator can emit is in it
        foreach (var hintName in new[]
        {
            "ConduitDispatch", "ConduitRegistration", "ConduitMediator", "ConduitRestEndpoints", "ConduitResilienceDispatch",
        })
        {
            FindGeneratedSource(output, hintName).Should().NotBeNull(hintName + " is emitted and compiled");
        }
    }

    // Runs the generator with both feature assemblies referenced, so the REST endpoints and the resilience dispatch are
    // emitted -- and compiled -- alongside the dispatch, registration and mediator.
    private static (Compilation Output, System.Collections.Immutable.ImmutableArray<Diagnostic> Generator) RunEveryEmitter(
        string source,
        params MetadataReference[] extraReferences)
        => RunGenerator(source, [HttpTransportReference, ResilienceReference, .. extraReferences]);
}
