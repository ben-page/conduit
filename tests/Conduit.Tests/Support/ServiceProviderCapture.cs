namespace Conduit.Tests.Support;

/// <summary>A scoped service that keeps the <see cref="IServiceProvider"/> injected into it.</summary>
public sealed class ServiceProviderCapture(IServiceProvider services)
{
    public IServiceProvider Services { get; } = services;
}
