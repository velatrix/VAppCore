namespace VAppCore.Tests;

public class CustomErrorsTests
{
    [Fact]
    public void ValidationError_HasCorrectStatusCode()
    {
        var error = new ValidationError(new ErrorObject { Message = "Invalid input" });

        Assert.Equal(422, error.StatusCode);
        Assert.Equal("Validation Error", error.Context.Title);
        Assert.Equal("server.errors.validation", error.Context.TitleKey);
    }

    [Fact]
    public void NotFoundError_HasCorrectStatusCode()
    {
        var error = new NotFoundError(new ErrorObject { Message = "User not found" });

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("Not Found Error", error.Context.Title);
    }

    [Fact]
    public void BadRequestError_HasCorrectStatusCode()
    {
        var error = new BadRequestError(new ErrorObject { Message = "Bad input" });

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Bad Request Error", error.Context.Title);
    }

    [Fact]
    public void UnauthorizedError_HasCorrectStatusCode()
    {
        var error = new UnauthorizedError(new ErrorObject { Message = "Not authenticated" });

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("Unauthorized Error", error.Context.Title);
    }

    [Fact]
    public void BusinessError_HasCorrectStatusCode()
    {
        var error = new BusinessError(new ErrorObject { Message = "Rule violation" });

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Business Error", error.Context.Title);
    }

    [Fact]
    public void SystemError_HasCorrectStatusCode()
    {
        var error = new SystemError(new ErrorObject { Message = "Unexpected failure" });

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("System Error", error.Context.Title);
    }

    [Fact]
    public void BaseError_Message_PropagatedToException()
    {
        var error = new NotFoundError(new ErrorObject { Message = "User 42 not found" });

        // Exception.Message should carry the error message
        Assert.Equal("User 42 not found", error.Message);
    }

    [Fact]
    public void BaseError_IsException_CanBeCaught()
    {
        var error = new ValidationError(new ErrorObject { Message = "fail" });

        Action act = () => throw error;
        var caught = Assert.Throws<ValidationError>(act);
        Assert.Equal(422, caught.StatusCode);
    }

    [Fact]
    public void BaseError_CatchAsBaseError_Works()
    {
        BaseError? caught = null;
        try
        {
            throw new NotFoundError(new ErrorObject { Message = "missing" });
        }
        catch (BaseError ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Equal(404, caught.StatusCode);
    }

    [Fact]
    public void ErrorObject_Metadata_CanHoldArbitraryData()
    {
        var error = new ValidationError(new ErrorObject
        {
            Message = "Invalid",
            MessageKey = "validation.invalid",
            Metadata = new { Field = "email", Constraint = "required" }
        });

        Assert.NotNull(error.Context.Error.Metadata);
        Assert.Equal("validation.invalid", error.Context.Error.MessageKey);
    }

    [Fact]
    public void ForbiddenError_HasCorrectStatusCode()
    {
        var error = new ForbiddenError(new ErrorObject { Message = "Access denied" });

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("Forbidden Error", error.Context.Title);
        Assert.Equal("server.errors.forbidden", error.Context.TitleKey);
    }

    [Fact]
    public void ConflictError_HasCorrectStatusCode()
    {
        var error = new ConflictError(new ErrorObject { Message = "Already exists" });

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Conflict Error", error.Context.Title);
        Assert.Equal("server.errors.conflict", error.Context.TitleKey);
    }
}
