using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers VAppCore services: ICurrentUser, MVC filters (VAuthorize, VResponse),
    /// VQueryParser model binder, validation factory, and a DbContext alias so that
    /// VServices can inject DbContext directly.
    /// Pair with <c>options.UseVAppCore&lt;...&gt;(sp)</c> on the DbContext registration.
    /// </summary>
    public static IServiceCollection AddVAppCore<TDbContext, TUserKey, TTenantKey>(
        this IServiceCollection services,
        Action<VAppCoreOptions>? configureOptions = null)
        where TDbContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        var optionsValue = new VAppCoreOptions();
        configureOptions?.Invoke(optionsValue);
        services.Configure<VAppCoreOptions>(o =>
        {
            o.UserIdClaim = optionsValue.UserIdClaim;
            o.TenantIdClaim = optionsValue.TenantIdClaim;
            o.RoleClaim = optionsValue.RoleClaim;
            o.PermissionClaim = optionsValue.PermissionClaim;
            o.EmailClaim = optionsValue.EmailClaim;
            o.CursorEncryptionKeys = optionsValue.CursorEncryptionKeys;
        });

        services.AddHttpContextAccessor();

        // ICurrentUser<TUserKey, TTenantKey> → ClaimsCurrentUser
        services.TryAddScoped<ICurrentUser<TUserKey, TTenantKey>, ClaimsCurrentUser<TUserKey, TTenantKey>>();
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<ICurrentUser<TUserKey, TTenantKey>>());

        // DbContext alias → TDbContext (so VServices can inject DbContext directly)
        services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TDbContext>());

        // Cursor protector: AesGcm if any keys configured, NoOp otherwise.
        // TryAddSingleton lets a custom ICursorProtector registered before AddVAppCore win.
        if (optionsValue.CursorEncryptionKeys != null && optionsValue.CursorEncryptionKeys.Count > 0)
        {
            var keyBytes = optionsValue.CursorEncryptionKeys
                .Select(Convert.FromBase64String)
                .ToList();
            services.TryAddSingleton<ICursorProtector>(_ => new AesGcmCursorProtector(keyBytes));
        }
        else
        {
            services.TryAddSingleton<ICursorProtector, NoOpCursorProtector>();
        }
        services.TryAddSingleton(sp => new CursorCodec(sp.GetRequiredService<ICursorProtector>()));

        // MVC filters and validation factory
        services.Configure<MvcOptions>(options =>
        {
            options.ModelBinderProviders.Insert(0, new VQueryParserBinderProvider());
            options.Filters.Add<VAuthorizeFilter>();
            options.Filters.Add<VResponseFilter>();
        });

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .ToDictionary(
                        e => e.Key,
                        e => e.Value!.Errors.Select(err => err.ErrorMessage).ToArray());

                return new ObjectResult(new ErrorContext
                {
                    Title = "Validation Error",
                    TitleKey = "server.errors.validation",
                    Error = new ErrorObject
                    {
                        Message = "One or more validation errors occurred.",
                        MessageKey = "server.errors.validation",
                        Metadata = errors
                    }
                })
                {
                    StatusCode = 422
                };
            };
        });

        return services;
    }

    /// <summary>
    /// Registers a single VService with auto-injected Db (resolved as DbContext) and CurrentUser.
    /// </summary>
    public static IServiceCollection AddVService<TService>(this IServiceCollection services)
        where TService : class
    {
        services.AddScoped(sp =>
        {
            var service = ActivatorUtilities.CreateInstance<TService>(sp);
            InjectVServiceDependencies(service, sp);
            return service;
        });
        return services;
    }

    /// <summary>
    /// Scans assembly for all classes inheriting VService and registers them with auto-injected
    /// Db (DbContext) and CurrentUser.
    /// </summary>
    public static IServiceCollection AddVServices(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!IsVService(type)) continue;

            services.AddScoped(type, sp =>
            {
                var service = ActivatorUtilities.CreateInstance(sp, type);
                InjectVServiceDependencies(service, sp);
                return service;
            });
        }
        return services;
    }

    /// <summary>
    /// Adds the VAppCore exception-handling middleware.
    /// Call early in the pipeline (before MapControllers / MapGet).
    /// </summary>
    public static IApplicationBuilder UseVAppCore(this IApplicationBuilder app)
    {
        app.UseMiddleware<VExceptionMiddleware>();
        return app;
    }

    // ── Internals ──

    private static bool IsVService(Type type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VService<,,,>))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static void InjectVServiceDependencies(object service, IServiceProvider sp)
    {
        var type = service.GetType();
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VService<,,,>))
            {
                InjectProperty(service, current, "Db", sp);
                InjectProperty(service, current, "CurrentUser", sp);
                InjectProperty(service, current, "CursorCodec", sp);
                return;
            }
            current = current.BaseType;
        }
    }

    private static void InjectProperty(object service, Type declaringType, string propertyName, IServiceProvider sp)
    {
        var prop = declaringType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return;
        var value = sp.GetRequiredService(prop.PropertyType);
        prop.SetValue(service, value);
    }
}
