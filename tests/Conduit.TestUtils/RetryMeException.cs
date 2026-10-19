namespace Conduit.TestUtils;

/// <summary>The failure <see cref="TestRetryPolicy"/> retries: a test handler throws it to ask for another attempt.</summary>
public sealed class RetryMeException : Exception
{
    public RetryMeException()
        : base("Retry me.")
    {
    }

    public RetryMeException(string message)
        : base(message)
    {
    }

    public RetryMeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
