using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record UploadBinaryCommand(string ScreenName, BinaryData Image) : ICommand<int>;
