using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Messages;

namespace Conduit.Runtime;

/// <summary>
/// Converts every closed set (<see cref="IClosedSet{TSelf}"/>) to and from its bare <see cref="IClosedSet{TSelf}.Value"/>
/// string. It is in <see cref="ConduitRuntime.JsonOptions"/>, so every Conduit wire, audit and job payload carries a
/// member as its value. A host that reads the wire with its own options -- a REST endpoint (ASP.NET's JSON options), a
/// client that mirrors <see cref="ConduitRuntime.JsonOptions"/> -- adds it there too, as it would
/// <see cref="JsonStringEnumConverter"/>.
/// </summary>
public sealed class ClosedSetJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsClass && Array.Exists(
            typeToConvert.GetInterfaces(),
            candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IClosedSet<>)
                && candidate.GetGenericArguments()[0] == typeToConvert);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        // DoNotWrapExceptions: the converter's own InvalidOperationException (a drifted or missing [ClosedSet]) is the
        // message the developer needs, not a TargetInvocationException around it.
        return (JsonConverter)Activator.CreateInstance(
            typeof(ClosedSetJsonConverter<>).MakeGenericType(typeToConvert),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DoNotWrapExceptions,
            binder: null,
            args: null,
            culture: CultureInfo.InvariantCulture)!;
    }
}
