using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record GetPngImageQuery(string Name) : IQuery<BinaryData?>;
