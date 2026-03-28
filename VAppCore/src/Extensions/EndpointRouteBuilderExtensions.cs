using Microsoft.AspNetCore.Builder;

namespace VAppCore;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Attaches a VQueryFilter configuration to a minimal API endpoint.
    /// This makes VQueryParser validate fields against the filter when bound.
    /// </summary>
    public static RouteHandlerBuilder WithQueryFilter<TFilter>(this RouteHandlerBuilder builder)
        where TFilter : IVQueryFilter, new()
    {
        return builder.WithMetadata(new UseVQueryParserAttribute(typeof(TFilter)));
    }
}
