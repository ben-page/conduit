using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record GetMissingFileQuery(string Name) : IQuery<BinaryData?>;
