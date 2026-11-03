using System.Globalization;

namespace Conduit.Runtime;

/// <summary>
/// Tells Conduit which language the reader of a dispatch's answer uses, from the dispatch's
/// <see cref="MessageContext"/> (its <see cref="MessageContext.Properties"/> are where an
/// <see cref="IDispatchContextEnricher"/> puts server-trusted values on every transport). The host registers it as a
/// service. Conduit calls it only where a refusal becomes a response, and never sets
/// <see cref="CultureInfo.CurrentCulture"/> or <see cref="CultureInfo.CurrentUICulture"/> from the answer.
/// </summary>
/// <remarks>
/// <para>Null means English: answer null for no context, a missing value, or a value the resolver does not know.
/// A transport may carry a client-chosen value when no enricher overwrites it, so an implementation must never trust
/// one it has not mapped (never build a <see cref="CultureInfo"/> from it) and should not throw; if it does, Conduit
/// logs the failure and answers in English.</para>
/// <para>Host code that needs the reader's language outside a response (a notification, an email) can call the same
/// resolver with <see cref="MessageContext.Current"/>.</para>
/// </remarks>
public interface IDispatchCultureResolver
{
    /// <summary>The reader's culture for the dispatch, or <see langword="null"/> for English.</summary>
    /// <param name="context">The dispatch's context, or <see langword="null"/> when there is none.</param>
    public CultureInfo? Resolve(MessageContext? context);
}
