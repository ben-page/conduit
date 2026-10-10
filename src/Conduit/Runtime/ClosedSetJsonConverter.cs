using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Runtime;

/// <summary>
/// One closed set's converter (<see cref="ClosedSetJsonConverterFactory"/> builds it once per type per options): writes a
/// member's <see cref="IClosedSet{TSelf}.Value"/> and reads a string back to the member that spells it (Ordinal), as a
/// value and as a dictionary key; any other token is a <see cref="JsonException"/>, which
/// <see cref="ConduitRuntime.DeserializeWire{T}"/> turns into a 400 naming the JSON path. Building it checks the type's
/// <see cref="ClosedSetAttribute"/> against <c>All</c>: TypeGen builds the client from the attribute and this converter
/// builds the wire from <c>All</c>, so a difference would be a client that disagrees with the server.
/// </summary>
/// <typeparam name="TSelf">The closed-set type.</typeparam>
internal sealed class ClosedSetJsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : class, IClosedSet<TSelf>
{
    private readonly Dictionary<string, TSelf> members = new(StringComparer.Ordinal);

    public ClosedSetJsonConverter()
    {
        var all = TSelf.All;
        foreach (var member in all)
        {
            if (!this.members.TryAdd(member.Value, member))
            {
                throw new InvalidOperationException(
                    $"{typeof(TSelf).FullName}.All spells '{member.Value}' twice; a closed set's values are distinct.");
            }
        }

        var declared = typeof(TSelf).GetCustomAttribute<ClosedSetAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"{typeof(TSelf).FullName} implements IClosedSet<{typeof(TSelf).Name}> without [ClosedSet(...)]: "
                + "Conduit.TypeGen reads a closed set's values only from that attribute.");

        if (!declared.Values.SequenceEqual(all.Select(member => member.Value), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{typeof(TSelf).FullName}: [ClosedSet] lists ({string.Join(", ", declared.Values)}) but All spells "
                + $"({string.Join(", ", all.Select(member => member.Value))}). The generated client is built from the "
                + "attribute and the wire from All -- list exactly All's values, in All's order.");
        }
    }

    public override TSelf Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        this.Find(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

    public override void Write(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Value);
    }

    public override TSelf ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        this.Find(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WritePropertyName(value.Value);
    }

    // The message never echoes the client's text; DeserializeWire answers with the JSON path only.
    private TSelf Find(string? value) =>
        value is not null && this.members.TryGetValue(value, out var member)
            ? member
            : throw new JsonException($"Not a {typeof(TSelf).Name}.");
}
