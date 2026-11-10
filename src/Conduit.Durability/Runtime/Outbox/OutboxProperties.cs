using System.Buffers;
using System.Text;
using System.Text.Json;
using Conduit.Durability.Configuration;
using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Writes the dispatch properties a host lists in <see cref="OutboxOptions.PersistedProperties"/> into an outbox
/// entry's <see cref="Persistence.Outbox.OutboxEntry.Properties"/> column, and reads them back for delivery. Both
/// delivery paths — the post-commit flush and <see cref="OutboxRelay"/> — restore the bag through <see cref="Read"/>,
/// so a relay retry of a failed flush runs under exactly the properties the flush did.
/// </summary>
/// <remarks>
/// The column is a JSON object, <c>{"key": value}</c>, written with <see cref="Utf8JsonWriter"/> rather than the
/// reflection serializer: a value is a JSON string (<see cref="string"/>, <see cref="Guid"/>), number
/// (<see cref="int"/>, <see cref="long"/>) or boolean, and its declared type, not the JSON, decides the CLR type it is
/// restored as.
/// </remarks>
internal static class OutboxProperties
{
    /// <summary>The value types a persisted property may declare.</summary>
    internal static readonly IReadOnlySet<Type> SupportedTypes =
        new HashSet<Type> { typeof(string), typeof(int), typeof(long), typeof(bool), typeof(Guid) };

    /// <summary>
    /// The JSON object of the <paramref name="persisted"/> keys present in <paramref name="properties"/> (a dispatch's
    /// <see cref="MessageContext.Properties"/>), or <see langword="null"/> when none is present. A key whose value is
    /// <see langword="null"/> counts as absent.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A listed key holds a value of another type than the one declared for it: the host stamps that key with a type
    /// its <see cref="OutboxOptions.PersistedProperties"/> does not say.
    /// </exception>
    internal static string? Write(IReadOnlyDictionary<string, object?>? properties, IDictionary<string, Type>? persisted)
    {
        if (properties is not { Count: > 0 } || persisted is not { Count: > 0 })
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        var written = 0;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, type) in persisted)
            {
                if (!properties.TryGetValue(key, out var value) || value is null)
                {
                    continue;
                }

                if (value.GetType() != type)
                {
                    throw new InvalidOperationException(
                        $"The dispatch property '{key}' is persisted as {type.FullName}, but its value is a " +
                        $"{value.GetType().FullName}. OutboxOptions.PersistedProperties must declare the type the " +
                        "host stores under the key.");
                }

                switch (value)
                {
                    case string text:
                        writer.WriteString(key, text);
                        break;
                    case int number:
                        writer.WriteNumber(key, number);
                        break;
                    case long number:
                        writer.WriteNumber(key, number);
                        break;
                    case bool flag:
                        writer.WriteBoolean(key, flag);
                        break;
                    case Guid id:
                        writer.WriteString(key, id);
                        break;
                    default:
                        // OutboxOptions validation admits only the types above.
                        throw new InvalidOperationException($"{type.FullName} is not a supported persisted property type.");
                }

                written++;
            }

            writer.WriteEndObject();
        }

        return written == 0 ? null : Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The properties stored in <paramref name="json"/>, each restored as the type <paramref name="persisted"/>
    /// declares for its key, or <see langword="null"/> when there are none — a NULL column (an entry written before
    /// the host listed any property) delivers with an empty bag, exactly as before. A stored key the host no longer
    /// lists, or whose value no longer reads as its declared type (the host changed it after the entry was written),
    /// is dropped rather than failing the delivery; so is the whole column when it is not a JSON object (truncated or
    /// edited by hand), which would otherwise fail every retry of the entry. The restored bag's names are
    /// case-insensitive, as every <see cref="MessageContext.Properties"/> bag's are.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? Read(string? json, IDictionary<string, Type>? persisted)
    {
        if (json is null || persisted is not { Count: > 0 })
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using var owned = document;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, object?>? restored = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (persisted.TryGetValue(property.Name, out var type) && TryReadValue(property.Value, type, out var value))
            {
                (restored ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase))[property.Name] = value;
            }
        }

        return restored;
    }

    private static bool TryReadValue(JsonElement element, Type type, out object? value)
    {
        value = null;
        if (type == typeof(string) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
        }
        else if (type == typeof(int) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            value = number;
        }
        else if (type == typeof(long) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var wide))
        {
            value = wide;
        }
        else if (type == typeof(bool) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
        }
        else if (type == typeof(Guid) && element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var id))
        {
            value = id;
        }

        return value is not null;
    }
}
