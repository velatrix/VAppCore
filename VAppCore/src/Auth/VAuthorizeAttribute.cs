namespace VAppCore;

/// <summary>
/// Permission and role-based authorization attribute.
/// Apply to controllers, actions, hubs or hub methods. Stackable — multiple attributes require ALL conditions.
/// Empty attribute = just requires authentication.
/// </summary>
/// <remarks>
/// <para>
/// Without <see cref="Scope"/>, <see cref="Permission"/>, <see cref="AnyOf"/> and <see cref="ApiKey"/> are read from
/// the caller's flat permission list (<see cref="ICurrentUser.HasPermission"/>).
/// </para>
/// <para>
/// With <see cref="Scope"/>, they are the caller's permissions <i>in one scope</i> — a project, an organization —
/// as the app's <see cref="IScopeAccessResolver"/> answers for it. A scope the caller may not see answers 404 (the
/// same answer as an id that was never issued); a missing permission answers 403. The scope's id comes from a route
/// value (or a hub method argument), directly or through an entity that belongs to the scope, and every other route
/// value registered as an entity of the scope must belong to it too. Scopes and entities are registered with
/// <see cref="VAuthorizationServiceCollectionExtensions.AddVAuthorization"/>.
/// </para>
/// <para>
/// Subclass to give declarations your own vocabulary (<c>[ProjectAccess(Permissions.X)]</c>): the filters read every
/// <see cref="VAuthorizeAttribute"/>, subclasses included.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class VAuthorizeAttribute : Attribute
{
    public string? Permission { get; set; }
    public string? Role { get; set; }

    /// <summary>
    /// When set, requires the caller is authenticated via the API key scheme AND the API key
    /// holds this permission. User cookies / JWTs are explicitly REJECTED on endpoints with
    /// <see cref="ApiKey"/> set, even if the user has the same permission.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>At least one of these permissions — in <see cref="Scope"/> when it is set.</summary>
    public string[]? AnyOf { get; set; }

    /// <summary>
    /// The registered scope type the permissions are held in (<c>"project"</c>). Null = the flat permission list.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Where the scope's id comes from: a route value, or for a hub method a parameter name. Defaults to the route value
    /// the scope was registered with. When it names an entity registered under the scope (<c>"runId"</c>), the scope is
    /// located through that entity, and a caller who may not see the scope gets the entity's 404.
    /// </summary>
    public string? ScopeFrom { get; set; }

    /// <summary>
    /// Route values of this endpoint that are deliberately not bound to the scope although an entity of the scope is
    /// registered under their name, and route values that are not ids at all — say why beside the declaration.
    /// <see cref="VAuthorizationCatalog"/> reports any other route value that is not bound.
    /// </summary>
    public string[]? Unbound { get; set; }
}
