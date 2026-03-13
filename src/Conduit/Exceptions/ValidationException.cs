namespace Conduit.Exceptions;

public class ValidationException(string message)
    : ConduitException("Validation", 422, message);
