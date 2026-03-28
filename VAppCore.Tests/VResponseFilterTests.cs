using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace VAppCore.Tests;

public class VResponseFilterTests
{
    private static ResultExecutingContext CreateContext(IActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        return new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            result,
            controller: null!);
    }

    private static Task NullNext() => Task.CompletedTask;
    private static ResultExecutionDelegate NextDelegate => () => Task.FromResult(new ResultExecutedContext(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
        new List<IFilterMetadata>(),
        new OkResult(),
        controller: null!));

    // ── VResponse allowed ──

    [Fact]
    public async Task VResponse_PassesThrough_AndUnwraps()
    {
        var filter = new VResponseFilter();
        var mapped = VResponse.Map(new { Id = 1, Secret = "x" }, e => new { e.Id });
        var result = new OkObjectResult(mapped);
        var context = CreateContext(result);

        await filter.OnResultExecutionAsync(context, NextDelegate);

        // Should unwrap — value is now the mapped data, not VResponse
        Assert.IsNotType<VResponse>(result.Value);
        var data = result.Value!;
        Assert.Equal(1, data.GetType().GetProperty("Id")!.GetValue(data));
    }

    // ── VPagedResponse allowed ──

    [Fact]
    public async Task VPagedResponse_PassesThrough()
    {
        var filter = new VResponseFilter();
        var paged = new VPagedResponse<object>
        {
            Items = [new { Id = 1 }],
            Page = 1,
            Size = 10,
            TotalItems = 1
        };
        var result = new OkObjectResult(paged);
        var context = CreateContext(result);

        await filter.OnResultExecutionAsync(context, NextDelegate);

        // Should pass through unchanged
        Assert.IsType<VPagedResponse<object>>(result.Value);
    }

    // ── Raw entity blocked ──

    [Fact]
    public async Task RawObject_Blocked_ThrowsSystemError()
    {
        var filter = new VResponseFilter();
        var result = new OkObjectResult(new { Id = 1, Name = "raw" });
        var context = CreateContext(result);

        var ex = await Assert.ThrowsAsync<SystemError>(
            () => filter.OnResultExecutionAsync(context, NextDelegate));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("VResponse", ex.Message);
    }

    [Fact]
    public async Task RawString_Blocked()
    {
        var filter = new VResponseFilter();
        var result = new OkObjectResult("just a string");
        var context = CreateContext(result);

        await Assert.ThrowsAsync<SystemError>(
            () => filter.OnResultExecutionAsync(context, NextDelegate));
    }

    [Fact]
    public async Task RawList_Blocked()
    {
        var filter = new VResponseFilter();
        var result = new OkObjectResult(new List<object> { new { Id = 1 } });
        var context = CreateContext(result);

        await Assert.ThrowsAsync<SystemError>(
            () => filter.OnResultExecutionAsync(context, NextDelegate));
    }

    // ── No body — not triggered ──

    [Fact]
    public async Task NoContent_NotTriggered()
    {
        var filter = new VResponseFilter();
        var result = new NoContentResult();
        var context = CreateContext(result);

        // Should not throw — NoContentResult is not ObjectResult
        await filter.OnResultExecutionAsync(context, NextDelegate);
    }

    [Fact]
    public async Task NullValue_NotTriggered()
    {
        var filter = new VResponseFilter();
        var result = new OkObjectResult(null);
        var context = CreateContext(result);

        // Should not throw — value is null
        await filter.OnResultExecutionAsync(context, NextDelegate);
    }

    // ── Error message is helpful ──

    [Fact]
    public async Task ErrorMessage_IncludesTypeName()
    {
        var filter = new VResponseFilter();
        var result = new OkObjectResult(new Dictionary<string, int>());
        var context = CreateContext(result);

        var ex = await Assert.ThrowsAsync<SystemError>(
            () => filter.OnResultExecutionAsync(context, NextDelegate));

        Assert.Contains("Dictionary", ex.Message);
        Assert.Contains("VResponse.Map()", ex.Message);
    }
}
