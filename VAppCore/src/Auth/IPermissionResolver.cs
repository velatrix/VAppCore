namespace VAppCore;

/// <summary>
/// Implement this to load roles/permissions from a database or external source.
/// Register in DI to override the default claims-based resolution.
/// </summary>
public interface IPermissionResolver<in TUserKey>
{
    Task<IReadOnlyList<string>> GetRolesAsync(TUserKey userId);
    Task<IReadOnlyList<string>> GetPermissionsAsync(TUserKey userId);
}
