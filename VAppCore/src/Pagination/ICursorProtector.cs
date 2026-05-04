namespace VAppCore;

/// <summary>
/// Encrypts/decrypts cursor payload bytes. Default implementations:
/// <see cref="NoOpCursorProtector"/> (passthrough — cursors are opaque to clients but not tamper-proof)
/// and <see cref="AesGcmCursorProtector"/> (AES-GCM authenticated encryption with random per-cursor nonce).
/// Implement and register a custom one in DI to use KMS / Key Vault / rotating keys.
/// </summary>
public interface ICursorProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Default protector when no encryption key is configured. Cursors are still opaque
/// (base64 of JSON) but not encrypted or tamper-resistant.
/// </summary>
public sealed class NoOpCursorProtector : ICursorProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}
