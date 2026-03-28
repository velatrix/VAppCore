using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace VAppCore;

public class VQueryParserBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var endpoint = ctx.HttpContext.GetEndpoint();

        if (endpoint != null)
        {
            var attribute = endpoint.Metadata.GetMetadata<UseVQueryParserAttribute>();

            if (attribute != null)
            {
                IVQueryFilter? filterConfig = null;

                if (attribute.FilterType != null)
                {
                    filterConfig = VQueryParser.FilterCache.GetOrAdd(attribute.FilterType, type =>
                        (IVQueryFilter)Activator.CreateInstance(type)!);
                }

                var parser = new VQueryParser(ctx.HttpContext.Request.Query, filterConfig);
                ctx.Result = ModelBindingResult.Success(parser);
                return Task.CompletedTask;
            }
        }

        ctx.Result = ModelBindingResult.Failed();
        return Task.CompletedTask;
    }
}

public class VQueryParserBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        return context.Metadata.ModelType == typeof(VQueryParser)
            ? new VQueryParserBinder()
            : null;
    }
}