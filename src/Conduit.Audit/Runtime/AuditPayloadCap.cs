using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Attributes;
using Conduit.Audit.Configuration;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Caps a sealed record's payload at <see cref="AuditOptions.MaxPayloadBytes"/> UTF-8 bytes. A body at or under the cap
/// is returned untouched. A body over it is re-written with its longest string values cut to one common length (the
/// largest that fits, found by binary search) and a top-level <c>"$truncated": { "bytes": N, "cap": C }</c> marker; when
/// the root is not an object, or no cut fits, the body is the marker alone. The result is always valid JSON within the cap.
/// </summary>
internal static class AuditPayloadCap
{
    internal const string MarkerName = "$truncated";

    private const string Ellipsis = "…";

    // A cut string's escaped length is its kept prefix's escaped length plus this. Escaping is per character (a cut
    // never splits a surrogate pair), so the two add.
    private static readonly int EllipsisBytes = EncodedLength(Ellipsis);

    /// <summary>Throws when <paramref name="maxPayloadBytes"/> is enabled (above 0) but below
    /// <see cref="AuditOptions.MinimumMaxPayloadBytes"/>, which the marker alone could exceed.</summary>
    internal static void ThrowIfInvalid(int maxPayloadBytes, string paramName)
    {
        if (maxPayloadBytes is > 0 and < AuditOptions.MinimumMaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                maxPayloadBytes,
                $"MaxPayloadBytes must be 0 or less (no cap) or at least {AuditOptions.MinimumMaxPayloadBytes}.");
        }
    }

    /// <summary>The payload to store under a cap of <paramref name="maxPayloadBytes"/> (0 or less: no cap).</summary>
    internal static JsonElement? Apply(JsonElement? payload, int maxPayloadBytes)
    {
        if (payload is not { } element || maxPayloadBytes <= 0 || element.ValueKind == JsonValueKind.Undefined)
        {
            return payload;
        }

        var raw = JsonMarshal.GetRawUtf8Value(element);
        if (raw.Length <= maxPayloadBytes)
        {
            return payload;
        }

        try
        {
            if (Shorten(raw, maxPayloadBytes) is { } shortened)
            {
                return shortened;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An unreadable or unwritable body (e.g. a lone surrogate escape) falls back to the marker alone.
        }

        return MarkerOnly(raw.Length, maxPayloadBytes);
    }

    private static JsonObject Marker(long originalBytes, int maxPayloadBytes) =>
        new() { ["bytes"] = originalBytes, ["cap"] = maxPayloadBytes };

    private static JsonElement MarkerOnly(long originalBytes, int maxPayloadBytes) =>
        ToElement(Write(new JsonObject { [MarkerName] = Marker(originalBytes, maxPayloadBytes) }));

    private static JsonElement? Shorten(ReadOnlySpan<byte> raw, int maxPayloadBytes)
    {
        if (JsonNode.Parse(raw) is not JsonObject root)
        {
            return null;
        }

        root.Remove(MarkerName);
        root.Insert(0, MarkerName, Marker(raw.Length, maxPayloadBytes));

        var slots = CollectStrings(root);
        var uncut = Write(root);
        if (uncut.WrittenCount <= maxPayloadBytes)
        {
            return ToElement(uncut);
        }

        // Every byte that is not a string value's content stays as written; only the string contents change.
        long fixedBytes = uncut.WrittenCount;
        var longest = 0;
        foreach (var slot in slots)
        {
            fixedBytes -= slot.EncodedBytes;
            longest = Math.Max(longest, slot.Value.Length);
        }

        // The size at cut length L is non-decreasing in L. Find the largest L that fits: lo fits, hi does not.
        if (SizeAt(0) > maxPayloadBytes)
        {
            return null;
        }

        int lo = 0, hi = longest;
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            if (SizeAt(mid) <= maxPayloadBytes)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        foreach (var slot in slots)
        {
            if (Cut(slot, lo) is { } cut)
            {
                slot.Replace(cut.Text);
            }
        }

        var written = Write(root);
        return written.WrittenCount <= maxPayloadBytes ? ToElement(written) : null;

        long SizeAt(int length)
        {
            var size = fixedBytes;
            foreach (var slot in slots)
            {
                size += Cut(slot, length)?.EncodedBytes ?? slot.EncodedBytes;
            }

            return size;
        }
    }

    // The slot cut to `length` characters plus the ellipsis, or null when that would not make it shorter on the wire.
    private static (string Text, int EncodedBytes)? Cut(StringSlot slot, int length)
    {
        if (slot.Value.Length <= length)
        {
            return null;
        }

        var keep = length > 0 && char.IsHighSurrogate(slot.Value[length - 1]) ? length - 1 : length;
        var encodedBytes = EncodedLength(slot.Value.AsSpan(0, keep)) + EllipsisBytes;
        return encodedBytes < slot.EncodedBytes ? (string.Concat(slot.Value.AsSpan(0, keep), Ellipsis), encodedBytes) : null;
    }

    // Every string value in the tree (objects and arrays, any depth). A redacted placeholder is
    // left out: it is already short, and a cut one would no longer read as "a value was here".
    private static List<StringSlot> CollectStrings(JsonObject root)
    {
        var slots = new List<StringSlot>();
        var pending = new Stack<JsonNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var container = pending.Pop();
            if (container is JsonObject jsonObject)
            {
                foreach (var (name, child) in jsonObject)
                {
                    Visit(child, node => jsonObject[name] = node);
                }
            }
            else if (container is JsonArray jsonArray)
            {
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var position = index;
                    Visit(jsonArray[index], node => jsonArray[position] = node);
                }
            }
        }

        return slots;

        void Visit(JsonNode? child, Action<JsonNode> replace)
        {
            if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                var text = value.GetValue<string>();
                if (text != AuditRedactedAttribute.Placeholder)
                {
                    slots.Add(new StringSlot(text, EncodedLength(text), replace));
                }
            }
            else if (child is JsonObject or JsonArray)
            {
                pending.Push(child);
            }
        }
    }

    // The escaped UTF-8 length of a string's content (no quotes), as Utf8JsonWriter's default encoder writes it.
    private static int EncodedLength(ReadOnlySpan<char> text) => JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;

    private static ArrayBufferWriter<byte> Write(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        return buffer;
    }

    private static JsonElement ToElement(ArrayBufferWriter<byte> buffer)
    {
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed record StringSlot(string Value, int EncodedBytes, Action<JsonNode> Setter)
    {
        public void Replace(string text) => this.Setter(JsonValue.Create(text));
    }
}
