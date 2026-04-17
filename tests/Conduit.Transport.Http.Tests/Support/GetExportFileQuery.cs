using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record GetExportFileQuery(string Range) : IQuery<ConduitFile?>;
