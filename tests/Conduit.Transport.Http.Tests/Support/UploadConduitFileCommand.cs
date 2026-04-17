using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record UploadConduitFileCommand(string ScreenName, ConduitFile Document) : ICommand<int>;
