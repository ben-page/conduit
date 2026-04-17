using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record GetOctetStreamQuery(string Name) : IQuery<BinaryData?>;
