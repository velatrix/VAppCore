using System.Text.Json;
using System.Text.Json.Serialization;

namespace VAppCore;

/// <summary>
/// Wire format for an opaque pagination cursor.
/// <c>Sort</c> is the canonical sort spec the cursor was created for (e.g. "-score,+name").
/// <c>Values</c> are the field values at the cursor position, in the same order as the sort
/// spec, with the entity Id appended last as a stable tiebreaker.
/// </summary>
public class CursorPayload
{
    [JsonPropertyName("s")]
    public string Sort { get; set; } = string.Empty;

    [JsonPropertyName("v")]
    public JsonElement[] Values { get; set; } = [];
}

/// <summary>
/// Thrown when a cursor cannot be decoded — malformed base64, decryption failure, tampered payload,
/// invalid JSON. Maps to a 400 response on the API surface.
/// </summary>
public class CursorDecodeException : Exception
{
    public CursorDecodeException(string message) : base(message) { }
    public CursorDecodeException(string message, Exception inner) : base(message, inner) { }
}
