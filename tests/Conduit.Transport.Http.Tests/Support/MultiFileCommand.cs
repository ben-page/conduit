using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record MultiFileCommand(string Name, BinaryData First, ConduitFile Second) : ICommand<int>;
