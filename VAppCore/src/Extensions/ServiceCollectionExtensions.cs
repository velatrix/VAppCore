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
    /// Registers all VAppCore services: ICurrentUser, VAuthorize filter, VQueryParser binder.
    /// Infers TKey, TUserKey, TTenantKey from the VDbContext base class of TDbContext.
    /// </summary>
    public static IServiceCollection AddVAppCore<TDbContext>(
        this IServiceCollection services,
        Action<VAppCoreAuthOptions>? configureAuth = null)
        where TDbContext : DbContext
    {
        // Resolve generic types from VDbContext<TKey, TUserKey, TTenantKey>
        var (keyType, userKeyType, tenantKeyType) = ResolveVDbContextTypes(typeof(TDbContext));

        // Auth options
        if (configureAuth is not null)
            services.Configure(configureAuth);
        else
            services.Configure<VAppCoreAuthOptions>(_ => { });

        // HttpContextAccessor
        services.AddHttpContextAccessor();

        // ICurrentUser<TUserKey, TTenantKey> → ClaimsCurrentUser<TUserKey, TTenantKey>
        var currentUserGenericInterface = typeof(ICurrentUser<,>).MakeGenericType(userKeyType, tenantKeyType);
        var currentUserImpl = typeof(ClaimsCurrentUser<,>).MakeGenericType(userKeyType, tenantKeyType);
        services.TryAddScoped(currentUserGenericInterface, currentUserImpl);

        // ICurrentUser (non-generic) → resolves from generic
        services.TryAddScoped<ICurrentUser>(sp => (ICurrentUser)sp.GetRequiredService(currentUserGenericInterface));

        // VDbContext<TKey, TUserKey, TTenantKey> → resolves to TDbContext
        var vDbContextType = typeof(VDbContext<,,>).MakeGenericType(keyType, userKeyType, tenantKeyType);
        services.TryAddScoped(vDbContextType, sp => sp.GetRequiredService<TDbContext>());

        // MVC: model binder + authorize filter
        services.Configure<MvcOptions>(options =>
        {
            options.ModelBinderProviders.Insert(0, new VQueryParserBinderProvider());
            options.Filters.Add<VAuthorizeFilter>();
            options.Filters.Add<VResponseFilter>();
        });

        // Validation: convert model state errors to ErrorContext format
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
    /// Registers a VService with auto-injected Db property.
    /// </summary>
    public static IServiceCollection AddVService<TService>(this IServiceCollection services)
        where TService : class
    {
        services.AddScoped(sp =>
        {
            var service = ActivatorUtilities.CreateInstance<TService>(sp);
            InjectDbContext(service, sp);
            return service;
        });
        return services;
    }

    /// <summary>
    /// Scans assembly for all classes inheriting VService and registers them with auto-injected Db.
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
                InjectDbContext(service, sp);
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

    private static (Type Key, Type UserKey, Type TenantKey) ResolveVDbContextTypes(Type dbContextType)
    {
        var current = dbContextType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VDbContext<,,>))
            {
                var args = current.GetGenericArguments();
                return (args[0], args[1], args[2]);
            }
            current = current.BaseType;
        }

        throw new InvalidOperationException(
            $"{dbContextType.Name} must inherit from VDbContext<TKey, TUserKey, TTenantKey>");
    }

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

    private static void InjectDbContext(object service, IServiceProvider sp)
    {
        var type = service.GetType();
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VService<,,,>))
            {
                var dbProp = current.GetProperty("Db", BindingFlags.Public | BindingFlags.Instance);
                if (dbProp is not null)
                {
                    var db = sp.GetRequiredService(dbProp.PropertyType);
                    dbProp.SetValue(service, db);
                }
                return;
            }
            current = current.BaseType;
        }
    }
}
