using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// Default ICurrentUser implementation that reads from ClaimsPrincipal.
/// Works with any auth mechanism that populates HttpContext.User (JWT, Cookie, OpenID Connect, Identity).
/// If IPermissionResolver is registered, uses it for roles/permissions instead of claims.
/// </summary>
public class ClaimsCurrentUser<TUserKey, TTenantKey> : ICurrentUser<TUserKey, TTenantKey>
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    private readonly IHttpContextAccessor _http;
    private readonly VAppCoreAuthOptions _options;
    private readonly IServiceProvider _sp;
    private IReadOnlyList<string>? _roles;
    private IReadOnlyList<string>? _permissions;

    public ClaimsCurrentUser(
        IHttpContextAccessor http,
        IOptions<VAppCoreAuthOptions> options,
        IServiceProvider sp)
    {
        _http = http;
        _options = options.Value;
        _sp = sp;
    }

    private ClaimsPrincipal? User => _http.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public TUserKey UserId
    {
        get
        {
            var claim = User?.FindFirst(_options.UserIdClaim)?.Value;
            return claim is null ? default! : ConvertKey<TUserKey>(claim);
        }
    }

    public TTenantKey TenantId
    {
        get
        {
            var claim = User?.FindFirst(_options.TenantIdClaim)?.Value;
            return claim is null ? default! : ConvertKey<TTenantKey>(claim);
        }
    }

    public string? Email => User?.FindFirst(_options.EmailClaim)?.Value;

    public IReadOnlyList<string> Roles => _roles ??= LoadRoles();
    public IReadOnlyList<string> Permissions => _permissions ??= LoadPermissions();

    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> LoadRoles()
    {
        var resolver = _sp.GetService<IPermissionResolver<TUserKey>>();
        if (resolver != null && UserId != null)
            return resolver.GetRolesAsync(UserId).GetAwaiter().GetResult();

        return User?.FindAll(_options.RoleClaim).Select(c => c.Value).ToList()
               ?? (IReadOnlyList<string>)[];
    }

    private IReadOnlyList<string> LoadPermissions()
    {
        var resolver = _sp.GetService<IPermissionResolver<TUserKey>>();
        if (resolver != null && UserId != null)
            return resolver.GetPermissionsAsync(UserId).GetAwaiter().GetResult();

        return User?.FindAll(_options.PermissionClaim).Select(c => c.Value).ToList()
               ?? (IReadOnlyList<string>)[];
    }

    private static T ConvertKey<T>(string value)
    {
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (type == typeof(Guid)) return (T)(object)Guid.Parse(value);
        if (type == typeof(int)) return (T)(object)int.Parse(value);
        if (type == typeof(long)) return (T)(object)long.Parse(value);
        if (type == typeof(string)) return (T)(object)value;

        return (T)Convert.ChangeType(value, type);
    }
}
