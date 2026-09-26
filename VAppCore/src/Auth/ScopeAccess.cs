namespace VAppCore;

/// <summary>
/// What a caller may do in one scope: nothing it may know of (<see cref="Hidden"/> — answered 404, like an id that was
/// never issued), or a set of permissions, possibly empty (<see cref="Visible"/> — a missing one is answered 403).
/// </summary>
public sealed class ScopeAccess
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The caller may not know the scope exists — including when it does not.</summary>
    public static ScopeAccess Hidden { get; } = new(false, None);

    /// <summary>The caller may see the scope and holds these permissions in it.</summary>
    public static ScopeAccess Visible(IEnumerable<string> permissions) =>
        new(true, new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase));

    private ScopeAccess(bool isVisible, HashSet<string> permissions)
    {
        IsVisible = isVisible;
        Permissions = permissions;
    }

    public bool IsVisible { get; }

    /// <summary>Compared case-insensitively, like <see cref="ICurrentUser.HasPermission"/>.</summary>
    public IReadOnlySet<string> Permissions { get; }

    public bool Has(string permission) => Permissions.Contains(permission);
}
