namespace Conduit.Exceptions;

/// <summary>
/// The catalog names of the two sentences Conduit itself writes and a person can see, so a host's catalog can
/// translate them. The English <see cref="Exception.Message"/> stays the source of truth inside the server; these
/// names are read only where a refusal becomes a response.
/// </summary>
public static class ConduitTextNames
{
    /// <summary>The answer to an unexpected failure. Values: none.</summary>
    public const string UnexpectedError = "conduit.unexpectedError";

    /// <summary>A request missing members of its contract. Values: <c>members</c> (the names, joined by ", ").</summary>
    public const string ContractMismatch = "conduit.contractMismatch";

    /// <summary>As <see cref="ContractMismatch"/> with some members only counted. Values: <c>members</c> and
    /// <c>more</c> (how many were left unnamed).</summary>
    public const string ContractMismatchMore = "conduit.contractMismatchMore";
}
