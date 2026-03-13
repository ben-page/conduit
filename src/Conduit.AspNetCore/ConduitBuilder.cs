using Microsoft.Extensions.DependencyInjection;

namespace Conduit.AspNetCore;

public class ConduitBuilder
{
    internal IServiceCollection Services { get; }

    public ConduitBuilder(IServiceCollection services)
    {
        Services = services;
    }
}
