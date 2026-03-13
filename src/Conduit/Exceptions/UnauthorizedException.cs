namespace Conduit.Exceptions;

public class UnauthorizedException(string message)
    : ConduitException("Unauthorized", 401, message);
