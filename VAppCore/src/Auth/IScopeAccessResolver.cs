using System.Security.Claims;

namespace VAppCore;

/// <summary>
/// The app's rule for who may do what where — the one place effective permissions are computed. Implement it and
/// register it in DI (scoped, when it reads a DbContext) to use <see cref="VAuthorizeAttribute.Scope"/>.
/// </summary>
/// <remarks>
/// VAppCore asks at most once per scope per request (or hub invocation) and keeps the answer for the rest of it —
/// never longer, so a grant or a removal takes effect on the caller's next request. A resolver must not call
/// <see cref="IVAccess"/>: it is what <see cref="IVAccess"/> calls.
/// </remarks>
public interface IScopeAccessResolver
{
    /// <returns>
    /// <see cref="ScopeAccess.Hidden"/> when the caller may not know the scope exists — including when it does not
    /// exist — else <see cref="ScopeAccess.Visible"/> with the permissions the caller holds in it (possibly none).
    /// </returns>
    ValueTask<ScopeAccess> ResolveAsync(ClaimsPrincipal caller, VScope scope, CancellationToken cancellationToken);
}
