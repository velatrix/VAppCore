namespace VAppCore;

/// <summary>
/// The caller's access to a scope, from the same resolver and the same per-request memo the declarations use — for
/// rules a declaration cannot express: a permission needed only for part of a request, linked data shown only to
/// holders of its own permission, or a ceiling (a grant may not exceed what its author holds).
/// </summary>
/// <remarks>
/// Scoped to the request (or hub invocation) and tied to its caller. Background work that acts for someone has no
/// caller here: it asks the app's own rule directly.
/// </remarks>
public interface IVAccess
{
    /// <summary>The caller's access to <paramref name="scope"/>, resolved once per request.</summary>
    ValueTask<ScopeAccess> GetAsync(VScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses as a declaration would — the scope's 404 when the caller may not see it, 403 when
    /// <paramref name="permission"/> is given and missing — and otherwise returns the access.
    /// </summary>
    ValueTask<ScopeAccess> RequireAsync(VScope scope, string? permission = null, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="RequireAsync"/>, with at least one of <paramref name="permissions"/>.</summary>
    ValueTask<ScopeAccess> RequireAnyAsync(VScope scope, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default);
}
