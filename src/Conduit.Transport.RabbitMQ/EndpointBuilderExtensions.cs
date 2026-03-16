using Conduit.Configuration;
using Conduit.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Extension methods for configuring RabbitMQ transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use RabbitMQ as the transport.
    /// Registers the transport as <see cref="IConduitTransport"/> (for event broadcast)
    /// and <see cref="IHostedService"/> (for background receiver lifecycle).
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="connectionString">AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/").</param>
    /// <param name="configure">Optional callback to configure RabbitMQ-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseRabbitMQ(
        this EndpointBuilder builder,
        string connectionString,
        Action<RabbitMQOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RabbitMQOptions { ConnectionString = connectionString };
        configure?.Invoke(options);

        // Register IServiceCollection so the transport can discover event listener registrations at startup
        builder.Services.AddSingleton<IServiceCollection>(builder.Services);

        // Register options and transport as singletons
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RabbitMQTransport>();

        // Register as IConduitTransport for event broadcast (used by generated mediator)
        builder.Services.AddSingleton<IConduitTransport>(serviceProvider =>
            serviceProvider.GetRequiredService<RabbitMQTransport>());

        // Register as IHostedService for background receiver lifecycle
        builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<RabbitMQTransport>());

        // Set a lazy-resolving proxy on the endpoint for outbound routing.
        // The endpoint needs a transport reference at configuration time,
        // but the actual transport instance is created by DI after the host is built.
        // The proxy is initialized by the RabbitMQTransport constructor.
        var proxy = new LazyTransportProxy();
        builder.Services.AddSingleton(proxy);
        builder.UseTransport(proxy);

        return builder;
    }

    /// <summary>
    /// Proxy transport that defers to the DI-resolved <see cref="RabbitMQTransport"/> on first use.
    /// Required because the endpoint needs a transport reference at configuration time,
    /// but the actual transport instance is created by DI after the service provider is built.
    /// Initialized by <see cref="RabbitMQTransport"/> in its constructor.
    /// </summary>
    internal sealed class LazyTransportProxy : IConduitTransport
    {
        private RabbitMQTransport? transport;

        public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
            => this.Resolve().SendRequestAsync(envelope, cancellationToken);

        public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
            => this.Resolve().SendEventAsync(envelope, cancellationToken);

        private RabbitMQTransport Resolve() =>
            this.transport ?? throw new InvalidOperationException(
                "RabbitMQ transport has not been initialized. " +
                "Ensure the host has started before sending messages.");

        internal void Initialize(RabbitMQTransport resolvedTransport)
        {
            this.transport = resolvedTransport;
        }
    }
}
