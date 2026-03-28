using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace VAppCore.Tests;

public class VExceptionMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task<(int StatusCode, string Body)> InvokeMiddleware(RequestDelegate handler)
    {
        var middleware = new VExceptionMiddleware(handler);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return (context.Response.StatusCode, body);
    }

    // ── No exception ──

    [Fact]
    public async Task NoException_PassesThrough()
    {
        var (status, body) = await InvokeMiddleware(_ =>
        {
            _.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.Equal(200, status);
    }

    // ── BaseError exceptions ──

    [Fact]
    public async Task NotFoundError_Returns404WithErrorContext()
    {
        var (status, body) = await InvokeMiddleware(_ =>
            throw new NotFoundError(new ErrorObject
            {
                Message = "User not found",
                MessageKey = "users.errors.notFound"
            }));

        Assert.Equal(404, status);

        var context = JsonSerializer.Deserialize<ErrorContext>(body, JsonOptions);
        Assert.NotNull(context);
        Assert.Equal("Not Found Error", context.Title);
        Assert.Equal("User not found", context.Error.Message);
    }

    [Fact]
    public async Task ValidationError_Returns422()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new ValidationError(new ErrorObject { Message = "Invalid" }));

        Assert.Equal(422, status);
    }

    [Fact]
    public async Task BadRequestError_Returns400()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new BadRequestError(new ErrorObject { Message = "Bad" }));

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task UnauthorizedError_Returns401()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new UnauthorizedError(new ErrorObject { Message = "Unauth" }));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task ForbiddenError_Returns403()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new ForbiddenError(new ErrorObject { Message = "Forbidden" }));

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task ConflictError_Returns409()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new ConflictError(new ErrorObject { Message = "Conflict" }));

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task BusinessError_Returns500()
    {
        var (status, _) = await InvokeMiddleware(_ =>
            throw new BusinessError(new ErrorObject { Message = "Business" }));

        Assert.Equal(500, status);
    }

    // ── Generic exception ──

    [Fact]
    public async Task GenericException_Returns500WithSystemError()
    {
        var (status, body) = await InvokeMiddleware(_ =>
            throw new InvalidOperationException("Something broke"));

        Assert.Equal(500, status);

        var context = JsonSerializer.Deserialize<ErrorContext>(body, JsonOptions);
        Assert.NotNull(context);
        Assert.Equal("System Error", context.Title);
        Assert.Equal("server.errors.system", context.TitleKey);
        Assert.Equal("Something broke", context.Error.Message);
    }

    // ── Content type ──

    [Fact]
    public async Task ErrorResponse_HasJsonContentType()
    {
        var middleware = new VExceptionMiddleware(_ =>
            throw new NotFoundError(new ErrorObject { Message = "test" }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        Assert.Equal("application/json", context.Response.ContentType);
    }
}
