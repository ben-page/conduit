namespace Conduit.Exceptions;

public class NotFoundException(string message)
    : ConduitException("NotFound", 404, message);
