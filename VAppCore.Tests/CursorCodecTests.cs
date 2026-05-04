using System.Text.Json;

namespace VAppCore.Tests;

public class CursorCodecTests
{
    // ── NoOp protector (no encryption) ──

    [Fact]
    public void RoundTrip_NoOp_PreservesPayload()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        var payload = new CursorPayload
        {
            Sort = "-score,+name",
            Values =
            [
                JsonSerializer.SerializeToElement(1500m),
                JsonSerializer.SerializeToElement("alice"),
                JsonSerializer.SerializeToElement(Guid.NewGuid())
            ]
        };

        var encoded = codec.Encode(payload);
        var decoded = codec.Decode(encoded);

        Assert.Equal(payload.Sort, decoded.Sort);
        Assert.Equal(payload.Values.Length, decoded.Values.Length);
    }

    [Fact]
    public void Decimal_RoundTripsExactly()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        var original = 12345.67890123456789m;
        var payload = new CursorPayload
        {
            Sort = "+price",
            Values = [JsonSerializer.SerializeToElement(original)]
        };

        var decoded = codec.Decode(codec.Encode(payload));
        var restored = decoded.Values[0].GetDecimal();

        Assert.Equal(original, restored);
    }

    [Fact]
    public void DateTimeOffset_RoundTripsExactly()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        var original = new DateTimeOffset(2026, 5, 4, 14, 30, 15, 123, TimeSpan.FromHours(3))
            .AddTicks(4567);
        var payload = new CursorPayload
        {
            Sort = "+createdAt",
            Values = [JsonSerializer.SerializeToElement(original)]
        };

        var decoded = codec.Decode(codec.Encode(payload));
        var restored = decoded.Values[0].GetDateTimeOffset();

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Encoded_IsValidBase64()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };

        var encoded = codec.Encode(payload);

        Convert.FromBase64String(encoded); // throws on invalid
    }

    // ── AES-GCM protector ──

    [Fact]
    public void RoundTrip_AesGcm_PreservesPayload()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var codec = new CursorCodec(new AesGcmCursorProtector(key));

        var payload = new CursorPayload
        {
            Sort = "-score",
            Values = [JsonSerializer.SerializeToElement(1500), JsonSerializer.SerializeToElement(Guid.NewGuid())]
        };

        var encoded = codec.Encode(payload);
        var decoded = codec.Decode(encoded);

        Assert.Equal(payload.Sort, decoded.Sort);
    }

    [Fact]
    public void AesGcm_TamperedCursor_Throws()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var codec = new CursorCodec(new AesGcmCursorProtector(key));

        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var encoded = codec.Encode(payload);

        // Flip a bit
        var bytes = Convert.FromBase64String(encoded);
        bytes[bytes.Length - 1] ^= 0x01;
        var tampered = Convert.ToBase64String(bytes);

        Assert.Throws<CursorDecodeException>(() => codec.Decode(tampered));
    }

    [Fact]
    public void AesGcm_WrongKey_Throws()
    {
        var key1 = new byte[32]; Random.Shared.NextBytes(key1);
        var key2 = new byte[32]; Random.Shared.NextBytes(key2);

        var codec1 = new CursorCodec(new AesGcmCursorProtector(key1));
        var codec2 = new CursorCodec(new AesGcmCursorProtector(key2));

        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var encoded = codec1.Encode(payload);

        Assert.Throws<CursorDecodeException>(() => codec2.Decode(encoded));
    }

    [Fact]
    public void AesGcm_DifferentNoncePerEncode()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var codec = new CursorCodec(new AesGcmCursorProtector(key));

        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var enc1 = codec.Encode(payload);
        var enc2 = codec.Encode(payload);

        Assert.NotEqual(enc1, enc2); // random nonce → different ciphertext
    }

    // ── Decode failures ──

    [Fact]
    public void Decode_GarbageInput_Throws()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        Assert.Throws<CursorDecodeException>(() => codec.Decode("not-base64-or-json!@#"));
    }

    [Fact]
    public void Decode_EmptyString_Throws()
    {
        var codec = new CursorCodec(new NoOpCursorProtector());
        Assert.Throws<CursorDecodeException>(() => codec.Decode(""));
    }

    [Fact]
    public void AesGcmCursorProtector_RequiresKeyLength32()
    {
        var shortKey = new byte[16];
        Assert.Throws<ArgumentException>(() => new AesGcmCursorProtector(shortKey));
    }

    // ── Multi-key (rotation) ──

    [Fact]
    public void MultiKey_DecryptsCursorEncryptedWithAnyKeyInList()
    {
        var key1 = new byte[32]; Random.Shared.NextBytes(key1);
        var key2 = new byte[32]; Random.Shared.NextBytes(key2);

        // Encode with old protector (only key1)
        var oldProtector = new AesGcmCursorProtector(key1);
        var oldCodec = new CursorCodec(oldProtector);
        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var encoded = oldCodec.Encode(payload);

        // New protector lists [key2, key1] — current is key2, key1 retained for rotation
        var rotatedProtector = new AesGcmCursorProtector([key2, key1]);
        var rotatedCodec = new CursorCodec(rotatedProtector);

        // Old cursor still decodes
        var decoded = rotatedCodec.Decode(encoded);
        Assert.Equal(payload.Sort, decoded.Sort);
    }

    [Fact]
    public void MultiKey_EncryptsWithFirstKey()
    {
        var key1 = new byte[32]; Random.Shared.NextBytes(key1);
        var key2 = new byte[32]; Random.Shared.NextBytes(key2);

        // New protector with [key1] (single)
        var single = new CursorCodec(new AesGcmCursorProtector(key1));
        // Multi protector with [key1, key2]
        var multi = new CursorCodec(new AesGcmCursorProtector([key1, key2]));

        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var encodedByMulti = multi.Encode(payload);

        // Single (only key1) decodes it → confirms multi encrypted with first key (key1)
        var decoded = single.Decode(encodedByMulti);
        Assert.Equal(payload.Sort, decoded.Sort);
    }

    [Fact]
    public void MultiKey_OnceOldKeyDropped_OldCursorsFailToDecode()
    {
        var oldKey = new byte[32]; Random.Shared.NextBytes(oldKey);
        var newKey = new byte[32]; Random.Shared.NextBytes(newKey);

        // Old cursor created with oldKey
        var oldCodec = new CursorCodec(new AesGcmCursorProtector(oldKey));
        var payload = new CursorPayload { Sort = "+id", Values = [JsonSerializer.SerializeToElement(1)] };
        var oldCursor = oldCodec.Encode(payload);

        // After rotation completes (only newKey), old cursor must fail
        var newOnly = new CursorCodec(new AesGcmCursorProtector(newKey));
        Assert.Throws<CursorDecodeException>(() => newOnly.Decode(oldCursor));
    }
}
