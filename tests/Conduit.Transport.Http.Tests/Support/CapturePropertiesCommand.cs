using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Command whose handler records the current dispatch's <c>MessageContext.Properties</c>,
/// so tests can assert what server-side enrichment contributed.
/// </summary>
public record CapturePropertiesCommand : ICommand<Guid>;
