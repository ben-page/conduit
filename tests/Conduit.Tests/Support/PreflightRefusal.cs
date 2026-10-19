using Conduit.Exceptions;

namespace Conduit.Tests.Support;

/// <summary>A typed refusal with data, so a test sees a check's refusal come back with its type and data intact.</summary>
public sealed class PreflightRefusal(string reason) : ConflictException("The preflight check refused it.")
{
    public string Reason => reason;
}
