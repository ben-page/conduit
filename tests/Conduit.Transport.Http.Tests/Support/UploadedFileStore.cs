using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class UploadedFileStore
{
    public string? ScreenName { get; set; }
    public BinaryData? Image { get; set; }
    public ConduitFile? File { get; set; }
}
