namespace Conduit.Messages
{
    /// <summary>A stand-in for Conduit's IQuery, declared here so this assembly depends on no Conduit package.</summary>
    public interface IQuery<TResult>
    {
    }
}

namespace Conduit.TypeGen.Tests.NetStandardContracts
{
    /// <summary>The result type the sample queries answer.</summary>
    public sealed class NetStandardDto
    {
    }

    /// <summary>A query whose result is declared <c>NetStandardDto?</c>.</summary>
    public sealed class NetStandardFindQuery : Conduit.Messages.IQuery<NetStandardDto?>
    {
    }

    /// <summary>A query whose result is declared not-null.</summary>
    public sealed class NetStandardRequireQuery : Conduit.Messages.IQuery<NetStandardDto>
    {
    }
}
