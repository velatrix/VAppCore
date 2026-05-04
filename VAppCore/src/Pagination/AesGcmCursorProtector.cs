using System.Security.Cryptography;

namespace VAppCore;

/// <summary>
/// AES-GCM authenticated encryption for cursor payloads. Provides confidentiality AND
/// tamper-detection in one primitive — clients can't read or modify the cursor.
/// Supports key rotation: encryption uses the first key in the list (current); decryption
/// tries each key in order. Deploy with [newKey, oldKey] to migrate cursors gracefully,
/// then later drop oldKey from the list.
/// Output layout: [12-byte nonce][16-byte tag][ciphertext], all concatenated then base64'd by the codec.
/// </summary>
public sealed class AesGcmCursorProtector : ICursorProtector
{
    private const int NonceSize = 12;          // GCM standard
    private const int TagSize = 16;            // GCM standard
    private const int RequiredKeySize = 32;    // 256-bit

    private readonly byte[][] _keys;

    /// <summary>Single-key constructor (no rotation).</summary>
    public AesGcmCursorProtector(byte[] key) : this([key]) { }

    /// <summary>
    /// Multi-key constructor. The first key is used for encryption (current).
    /// All keys are tried in order during decryption (current first, then older keys).
    /// </summary>
    public AesGcmCursorProtector(IList<byte[]> keys)
    {
        if (keys == null || keys.Count == 0)
            throw new ArgumentException("At least one key is required.", nameof(keys));
        foreach (var k in keys)
            if (k.Length != RequiredKeySize)
                throw new ArgumentException(
                    $"AES-GCM key must be exactly {RequiredKeySize} bytes (256-bit). Got {k.Length}.", nameof(keys));
        _keys = keys.ToArray();
    }

    public byte[] Protect(byte[] plaintext) => ProtectWithKey(_keys[0], plaintext);

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            throw new CursorDecodeException("Cursor payload too short to be a valid AES-GCM cursor.");

        Exception? lastFailure = null;
        foreach (var key in _keys)
        {
            try
            {
                return UnprotectWithKey(key, ciphertext);
            }
            catch (CursorDecodeException ex)
            {
                lastFailure = ex;
            }
        }
        throw new CursorDecodeException(
            "Cursor decryption failed against every configured key (wrong key, tampered, or invalid payload).",
            lastFailure!);
    }

    private static byte[] ProtectWithKey(byte[] key, byte[] plaintext)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    private static byte[] UnprotectWithKey(byte[] key, byte[] ciphertext)
    {
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var encrypted = new byte[ciphertext.Length - NonceSize - TagSize];

        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(ciphertext, NonceSize + TagSize, encrypted, 0, encrypted.Length);

        var plaintext = new byte[encrypted.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, encrypted, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CursorDecodeException("AES-GCM decryption failed for this key.", ex);
        }
        return plaintext;
    }
}
