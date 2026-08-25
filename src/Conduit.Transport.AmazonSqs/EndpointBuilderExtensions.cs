using Amazon;
using Amazon.Runtime;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Extension methods for configuring Amazon SQS (+SNS) transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use Amazon SQS (+SNS) for messaging.
    /// Authentication uses the default AWS credential chain (environment, profile, or IAM role).
    /// Registers the transport as <see cref="IConduitTransport"/> for event broadcast
    /// and <see cref="IHostedService"/> for background receive lifecycle.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="configure">Optional callback to configure transport options (Region, ServiceUri, etc.).</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseAmazonSqs(
        this EndpointBuilder builder,
        Action<AmazonSqsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AmazonSqsOptions { EndpointName = builder.EndpointName };

        // Region and Credentials are excluded from the bind and mapped by hand: RegionEndpoint has
        // no constructor the binder can use (it binds null silently) and AWSCredentials is abstract
        // (the binder throws outright).
        ConduitConfigurationBinder.BindTransportOptions(
            options, builder.Configuration, "Region", "Credentials");
        ApplyAwsConfiguration(options, builder.Configuration);

        configure?.Invoke(options);

        var endpointName = builder.EndpointName;

        // Options are keyed by endpoint — never non-keyed, or a second endpoint on this
        // transport would silently overwrite the first.
        builder.Services.AddKeyedSingleton(endpointName, options);

        // One transport instance per endpoint. ActivatorUtilities keeps constructor injection
        // automatic for ConduitOptions, IServiceProvider and ILogger<T>; only the options are
        // passed explicitly, resolved through this endpoint's key.
        builder.Services.AddKeyedSingleton<AmazonSqsTransport>(
            endpointName,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<AmazonSqsTransport>(
                serviceProvider, serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>(key)));

        // One broadcast target and one lifecycle per endpoint, each forwarding to its own instance
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<AmazonSqsTransport>(endpointName));
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<AmazonSqsTransport>(endpointName));

        return builder.UseTransport<AmazonSqsTransport>();
    }

    /// <summary>
    /// Maps the configuration keys the binder cannot construct: <c>Region</c> by AWS system name,
    /// and <c>Credentials</c> into static credentials when both key parts are present.
    /// </summary>
    private static void ApplyAwsConfiguration(AmazonSqsOptions options, IConfiguration? section)
    {
        if (section is null)
        {
            return;
        }

        if (section["Region"] is { Length: > 0 } regionName)
        {
            options.Region = RegionEndpoint.GetBySystemName(regionName);
        }

        var accessKey = section["Credentials:AccessKey"];
        var secretKey = section["Credentials:SecretKey"];

        if (accessKey is not { Length: > 0 } || secretKey is not { Length: > 0 })
        {
            // No static credentials configured — the default AWS credential chain applies.
            return;
        }

        var sessionToken = section["Credentials:SessionToken"];

        options.Credentials = sessionToken is { Length: > 0 }
            ? new SessionAWSCredentials(accessKey, secretKey, sessionToken)
            : new BasicAWSCredentials(accessKey, secretKey);
    }
}
