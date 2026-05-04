using System.Text;
using System.Text.Json;

namespace VAppCore;

/// <summary>
/// Encodes/decodes <see cref="CursorPayload"/> to/from base64 strings, optionally
/// passing through an <see cref="ICursorProtector"/> for encryption.
/// JSON serialization uses <c>JsonNumberHandling.WriteAsString | AllowReadingFromString</c>
/// so that decimal precision survives round-trip.
/// </summary>
public sealed class CursorCodec
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly ICursorProtector _protector;

    public CursorCodec(ICursorProtector protector)
    {
        _protector = protector;
    }

    public string Encode(CursorPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var protectedBytes = _protector.Protect(json);
        return Convert.ToBase64String(protectedBytes);
    }

    public CursorPayload Decode(string cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            throw new CursorDecodeException("Cursor is empty.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(cursor); }
        catch (FormatException ex) { throw new CursorDecodeException("Cursor is not valid base64.", ex); }

        var unprotected = _protector.Unprotect(bytes);

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(unprotected, JsonOpts);
            if (payload is null)
                throw new CursorDecodeException("Cursor payload is null after deserialization.");
            return payload;
        }
        catch (JsonException ex)
        {
            throw new CursorDecodeException("Cursor JSON is malformed.", ex);
        }
    }
}
