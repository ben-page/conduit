using Conduit.Exceptions;

namespace Conduit.Runtime;

/// <summary>
/// Maps an exception a handler let escape to the refusal a wire caller should see, before
/// <see cref="ConduitRuntime"/>'s wire dispatch
/// answers <c>InternalError</c>. For a persistence library's exceptions Conduit cannot name (it references no data
/// layer): register one per library in DI. Return <see langword="null"/> to decline.
/// </summary>
public interface IWireExceptionTranslator
{
    /// <summary>The refusal for <paramref name="exception"/>, or <see langword="null"/> when this translator does not claim it.</summary>
    public ConduitException? Translate(Exception exception);
}
