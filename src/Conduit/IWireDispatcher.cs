using System.Text.Json;

namespace Conduit;

public interface IWireDispatcher
{
    Task<object?> DispatchAsync(string type, JsonElement payload, CancellationToken cancellationToken);
}
