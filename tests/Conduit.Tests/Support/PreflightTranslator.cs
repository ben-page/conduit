using Conduit.Exceptions;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>Claims a <see cref="TimeoutException"/> as a 412, as a host maps its data layer's concurrency exception.</summary>
public sealed class PreflightTranslator : IWireExceptionTranslator
{
    public ConduitException? Translate(Exception exception) =>
        exception is TimeoutException ? new PreconditionFailedException("The row changed.") : null;
}
