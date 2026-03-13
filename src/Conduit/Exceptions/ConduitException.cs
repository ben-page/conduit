namespace Conduit.Exceptions;

public abstract class ConduitException(string code, int statusCode, string message)
    : Exception(message)
{
    public string Code => code;
    public int StatusCode => statusCode;
}
