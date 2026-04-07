namespace Conduit.Resilience.Tests.Support;

public class FailingHandler
{
    private readonly Queue<Exception?> outcomes;

    public int CallCount { get; private set; }

    public FailingHandler(params Exception?[] outcomes)
    {
        this.outcomes = new Queue<Exception?>(outcomes);
    }

    public Task<string> HandleAsync()
    {
        this.CallCount++;
        var outcome = this.outcomes.Count > 0 ? this.outcomes.Dequeue() : null;
        if (outcome is not null)
        {
            throw outcome;
        }

        return Task.FromResult("success");
    }
}
