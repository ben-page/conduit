using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record GenerateThumbnailCommand(string SourceId) : ICommand<BinaryData>;
