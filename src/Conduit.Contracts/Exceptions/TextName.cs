namespace Conduit.Exceptions;

/// <summary>
/// A value of a <see cref="RefusalText"/> that is itself a word or phrase: the catalog translates it by name before
/// it fills the sentence. The English <see cref="Exception.Message"/> stays the source of truth inside the server;
/// this is read only where a refusal becomes a response.
/// </summary>
/// <param name="Name">The catalog name of the word or phrase.</param>
public sealed record TextName(string Name);
