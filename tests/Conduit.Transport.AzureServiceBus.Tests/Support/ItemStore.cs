namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public class ItemStore
{
    private readonly Dictionary<Guid, string> items = new();

    public void Add(Guid id, string name) => this.items[id] = name;
    public string? Get(Guid id) => this.items.TryGetValue(id, out var name) ? name : null;
    public void Remove(Guid id) => this.items.Remove(id);
}
