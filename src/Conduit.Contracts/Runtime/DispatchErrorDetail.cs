namespace Conduit.Runtime;

/// <summary>
/// A single error detail within a failed dispatch. <paramref name="Field"/> names the field for a
/// validation error and is <see langword="null"/> for an object-level one.
/// </summary>
/// <remarks>
/// <b>It lives in <c>Conduit.Contracts</c>, not beside <c>DispatchResult</c>, and the namespace is
/// deliberately unchanged.</b> <see cref="Conduit.Exceptions.ConduitException"/> carries these so a
/// throw site can put operands on the wire, and <c>ConduitException</c> is in this dependency-free
/// assembly -- declaring the record in <c>Conduit</c> would make <c>Conduit.Contracts</c> reference
/// <c>Conduit</c>, which is a cycle. Keeping the <c>Conduit.Runtime</c> namespace is the same idiom
/// <c>Conduit.Exceptions</c>/<c>Conduit.Messages</c> already use here (see the csproj comment and
/// CLAUDE.md's "Namespace layout"): namespaces do not follow assembly boundaries in this repo, so no
/// call site changes. The record is positional over <c>string?</c>/<c>string</c> and adds no package
/// reference, so the zero-dependency guarantee holds.
/// </remarks>
/// <param name="Field">The field that caused the error, or <see langword="null"/> for object-level errors.</param>
/// <param name="Message">A human-readable description of the error.</param>
public sealed record DispatchErrorDetail(string? Field, string Message);
