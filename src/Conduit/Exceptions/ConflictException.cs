namespace Conduit.Exceptions;

public class ConflictException(string message)
    : ConduitException("Conflict", 409, message);
