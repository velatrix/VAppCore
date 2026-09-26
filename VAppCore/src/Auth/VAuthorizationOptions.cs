using System.Globalization;

namespace VAppCore;

/// <summary>
/// Finds the scope an entity belongs to: the scope's id for the entity with this id, or null when there is no such
/// entity. Called with the request's (or hub invocation's) services, so it may read a scoped DbContext.
/// </summary>
public delegate ValueTask<string?> VEntityLocator(IServiceProvider services, string id, CancellationToken cancellationToken);

/// <summary>What a 403 is about, for <see cref="VAuthorizationOptions.Forbidden"/>.</summary>
/// <param name="Scope">The scope the permissions were looked for in.</param>
/// <param name="Permissions">The permission that was missing — or, when <paramref name="AnyOf"/>, all of which were.</param>
/// <param name="AnyOf">One of <paramref name="Permissions"/> would have been enough.</param>
public sealed record VForbidden(VScope Scope, IReadOnlyList<string> Permissions, bool AnyOf);

/// <summary>
/// The scopes permissions are held in, and the entities that belong to them. Configure with
/// <see cref="VAuthorizationServiceCollectionExtensions.AddVAuthorization"/>.
/// </summary>
public sealed class VAuthorizationOptions
{
    private readonly Dictionary<string, VScopeRegistration> _scopes = new(StringComparer.Ordinal);

    /// <summary>The scopes, in registration order.</summary>
    public IReadOnlyCollection<VScopeRegistration> Scopes => _scopes.Values;

    /// <summary>
    /// The <see cref="System.Security.Principal.IIdentity.AuthenticationType"/> an API-key caller carries;
    /// <see cref="VAuthorizeAttribute.ApiKey"/> requires it. Defaults to VAppCore's own scheme name.
    /// </summary>
    public string ApiKeyAuthenticationType { get; set; } = ApiKeyAuthenticationHandler.SchemeName;

    /// <summary>
    /// The error a scoped declaration answers (as 403) when a permission is missing. Default: <c>permission.required</c>
    /// with <c>{ permission }</c>, or <c>{ anyOf }</c> for <see cref="VAuthorizeAttribute.AnyOf"/>.
    /// </summary>
    public Func<VForbidden, ErrorObject>? Forbidden { get; set; }

    /// <summary>A scope with one instance — the whole installation, say. Declarations name it with no id.</summary>
    /// <param name="notFound">Answered (404) when the resolver hides it; a singleton scope is usually never hidden.</param>
    public VAuthorizationOptions AddScope(string type, ErrorObject? notFound = null)
    {
        Add(new VScopeRegistration(type, routeValue: null, notFound ?? DefaultNotFound, canonicalize: _ => null));
        return this;
    }

    /// <summary>
    /// A scope whose id is a <typeparamref name="TKey"/>, carried by default in the route value
    /// <paramref name="routeValue"/>. Ids are compared in the key type's canonical form, so <c>{projectId}</c> in any
    /// format the route accepts names the same scope.
    /// </summary>
    /// <param name="notFound">Answered (404) for an id that does not parse, a scope that does not exist, and a scope the
    /// caller may not see — all three alike.</param>
    public VAuthorizationOptions AddScope<TKey>(string type, string routeValue, ErrorObject notFound)
        where TKey : IParsable<TKey>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeValue);
        ArgumentNullException.ThrowIfNull(notFound);
        Add(new VScopeRegistration(type, routeValue, notFound, Canonical<TKey>));
        return this;
    }

    /// <summary>
    /// An entity that belongs to a scope, named in routes (or hub method parameters) by <paramref name="key"/>. Every
    /// route value of that name on an endpoint that declares the scope must belong to the scope, or the endpoint
    /// answers <paramref name="notFound"/> — exactly what an id that was never issued gets. A declaration can also
    /// locate its scope through the entity (<see cref="VAuthorizeAttribute.ScopeFrom"/> = <paramref name="key"/>).
    /// </summary>
    public VAuthorizationOptions AddEntity(string scope, string key, VEntityLocator locate, ErrorObject notFound)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(notFound);

        if (!_scopes.TryGetValue(scope, out var registration))
            throw new InvalidOperationException($"Entity '{key}' names scope '{scope}', which is not registered (AddScope first).");
        if (registration.RouteValue is null)
            throw new InvalidOperationException($"Scope '{scope}' has no id, so no entity can belong to it.");
        if (string.Equals(registration.RouteValue, key, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{key}' is scope '{scope}''s own route value, not an entity of it.");
        if (!registration.AddEntity(new VEntityRegistration(registration, key, locate, notFound)))
            throw new InvalidOperationException($"Entity '{key}' is already registered under scope '{scope}'.");
        return this;
    }

    /// <summary>
    /// <see cref="AddEntity(string, string, VEntityLocator, ErrorObject)"/> with the entity's id parsed as a
    /// <typeparamref name="TKey"/>; an id that does not parse is an entity that does not exist.
    /// </summary>
    public VAuthorizationOptions AddEntity<TKey>(
        string scope, string key,
        Func<IServiceProvider, TKey, CancellationToken, ValueTask<string?>> locate,
        ErrorObject notFound)
        where TKey : IParsable<TKey>
    {
        ArgumentNullException.ThrowIfNull(locate);
        return AddEntity(scope, key, (services, id, ct) =>
            TKey.TryParse(id, CultureInfo.InvariantCulture, out var parsed)
                ? locate(services, parsed, ct)
                : ValueTask.FromResult<string?>(null), notFound);
    }

    /// <summary>The registration of <paramref name="type"/>, or null.</summary>
    public VScopeRegistration? FindScope(string type) => _scopes.GetValueOrDefault(type);

    internal VScopeRegistration Scope(string type) =>
        FindScope(type) ?? throw new InvalidOperationException(
            $"[VAuthorize] names scope '{type}', which AddVAuthorization did not register.");

    internal ErrorObject ForbiddenError(VForbidden forbidden)
    {
        if (Forbidden is not null)
            return Forbidden(forbidden);

        return forbidden is { AnyOf: false, Permissions.Count: 1 }
            ? new ErrorObject
            {
                Message = $"Required permission: {forbidden.Permissions[0]}",
                MessageKey = "permission.required",
                Metadata = new { permission = forbidden.Permissions[0] }
            }
            : new ErrorObject
            {
                Message = $"Required permission: one of {string.Join(", ", forbidden.Permissions)}",
                MessageKey = "permission.required",
                Metadata = new { anyOf = forbidden.Permissions }
            };
    }

    private static readonly ErrorObject DefaultNotFound = new()
    {
        Message = "Not found",
        MessageKey = "server.errors.missingResource"
    };

    private void Add(VScopeRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Type);
        if (!_scopes.TryAdd(registration.Type, registration))
            throw new InvalidOperationException($"Scope '{registration.Type}' is already registered.");
    }

    private static string? Canonical<TKey>(string value) where TKey : IParsable<TKey> =>
        TKey.TryParse(value, CultureInfo.InvariantCulture, out var key)
            ? Convert.ToString(key, CultureInfo.InvariantCulture)
            : null;
}

/// <summary>A registered scope: its type, the route value that carries its id, its 404, and its entities.</summary>
public sealed class VScopeRegistration
{
    private readonly Dictionary<string, VEntityRegistration> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _canonicalize;

    internal VScopeRegistration(string type, string? routeValue, ErrorObject notFound, Func<string, string?> canonicalize)
    {
        Type = type;
        RouteValue = routeValue;
        NotFound = notFound;
        _canonicalize = canonicalize;
    }

    public string Type { get; }

    /// <summary>Null for a singleton scope.</summary>
    public string? RouteValue { get; }

    public ErrorObject NotFound { get; }

    /// <summary>Keyed by the route value that names the entity, compared case-insensitively like route values.</summary>
    public IReadOnlyDictionary<string, VEntityRegistration> Entities => _entities;

    /// <summary>The id in its canonical form, or null when it is not an id of this scope's key type.</summary>
    public string? Canonicalize(string id) => _canonicalize(id);

    internal bool AddEntity(VEntityRegistration entity) => _entities.TryAdd(entity.Key, entity);
}

/// <summary>An entity registered under a scope.</summary>
public sealed class VEntityRegistration
{
    internal VEntityRegistration(VScopeRegistration scope, string key, VEntityLocator locate, ErrorObject notFound)
    {
        Scope = scope;
        Key = key;
        Locate = locate;
        NotFound = notFound;
    }

    public VScopeRegistration Scope { get; }
    public string Key { get; }
    public VEntityLocator Locate { get; }
    public ErrorObject NotFound { get; }

    /// <summary>The canonical id of the scope the entity belongs to, or null when there is no such entity.</summary>
    public async ValueTask<string?> LocateScopeAsync(IServiceProvider services, string id, CancellationToken cancellationToken)
    {
        var owner = await Locate(services, id, cancellationToken);
        return owner is null ? null : Scope.Canonicalize(owner);
    }
}
