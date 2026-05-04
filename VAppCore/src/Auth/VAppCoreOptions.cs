using System.Security.Claims;

namespace VAppCore;

/// <summary>
/// VAppCore configuration. Holds claim names for ICurrentUser, the cursor encryption key,
/// and any other library-wide options. Configure via the action passed to
/// <see cref="ServiceCollectionExtensions.AddVAppCore{TDbContext, TUserKey, TTenantKey}"/>.
/// </summary>
public class VAppCoreOptions
{
    public string UserIdClaim { get; set; } = "sub";
    public string TenantIdClaim { get; set; } = "tenant_id";
    public string RoleClaim { get; set; } = ClaimTypes.Role;
    public string PermissionClaim { get; set; } = "permission";
    public string EmailClaim { get; set; } = "email";

    /// <summary>
    /// Optional list of 32-byte (256-bit) AES-GCM keys, each base64-encoded.
    /// Encryption uses the FIRST key (current). Decryption tries each key in order,
    /// supporting key rotation: deploy with <c>[newKey, oldKey]</c> so existing cursors
    /// keep decrypting; later drop <c>oldKey</c> from the list.
    /// When null or empty, cursors are unencrypted (opaque base64 of JSON, but tamperable).
    /// Override the protector entirely by registering a custom <see cref="ICursorProtector"/> in DI
    /// (TryAddSingleton wins over AddVAppCore's default registration).
    /// </summary>
    public IList<string>? CursorEncryptionKeys { get; set; }
}
