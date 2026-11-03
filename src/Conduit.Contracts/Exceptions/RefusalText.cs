namespace Conduit.Exceptions;

/// <summary>
/// The name of the sentence a refusal says, and the values that fill it, so a host can write the same refusal in
/// the reader's language. The English <see cref="Exception.Message"/> stays the source of truth inside the server
/// (logs, activity status, audit); this is read only where a refusal becomes a response.
/// </summary>
/// <param name="Name">The catalog name of the sentence, e.g. <c>orders.shipped</c>.</param>
/// <param name="Values">The values that fill the sentence, keyed by placeholder name, or <see langword="null"/>
/// when the sentence has none. A value that is itself a word the catalog translates is a <see cref="TextName"/>.</param>
public sealed record RefusalText(string Name, IReadOnlyDictionary<string, object?>? Values = null);
