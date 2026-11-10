using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>A class command with settable properties and no constructor parameters, uploaded as multipart.</summary>
public class AttachDocumentCommand : ICommand<string>
{
    public string Title { get; set; } = string.Empty;

    public ItemStatus Status { get; set; }

    public ConduitFile Document { get; set; } = null!;
}
