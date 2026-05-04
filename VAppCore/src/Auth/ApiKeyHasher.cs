using System.Security.Cryptography;
using System.Text;

namespace VAppCore;

/// <summary>
/// Generates and hashes API key plaintexts. Format: "vk_live_" + 43 base64-url chars
/// (32 random bytes, no padding). Hash: SHA-256 hex (lowercase, 64 chars). High-entropy
/// keys (256 bits) make plain SHA-256 sufficient — no need for slow KDFs.
/// </summary>
public static class ApiKeyHasher
{
    private const string Prefix = "vk_live_";
    private const int RandomBytes = 32;
    private const int PrefixDisplayLength = 12;

    /// <summary>Generates a new plaintext key. Format: vk_live_&lt;43 base64-url chars&gt;.</summary>
    public static string GeneratePlaintext()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        var b64 = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + b64;
    }

    /// <summary>SHA-256 hex (lowercase) of the plaintext. Used for indexed lookup.</summary>
    public static string Hash(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>First 12 chars of the plaintext for admin display (e.g. "vk_live_a1b2").</summary>
    public static string ExtractPrefix(string plaintext)
        => plaintext.Length <= PrefixDisplayLength ? plaintext : plaintext[..PrefixDisplayLength];
}
