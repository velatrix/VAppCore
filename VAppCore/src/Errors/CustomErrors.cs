namespace VAppCore;

public class ErrorObject
{
    public string Message { get; set; } = string.Empty;
    public string MessageKey { get; set; } = string.Empty;
    public object? Metadata { get; set; }
}

public class ErrorContext
{
    public string Title { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public required ErrorObject Error { get; set; }
}

public class BaseError : Exception
{
    public int StatusCode { get; }
    public ErrorContext Context { get; }

    public BaseError(int statusCode, ErrorContext context)
        : base(context.Error.Message)
    {
        StatusCode = statusCode;
        Context = context;
    }
}

public class ValidationError : BaseError
{
    public ValidationError(ErrorObject error)
        : base(422, new ErrorContext
        {
            Title = "Validation Error",
            TitleKey = "server.errors.validation",
            Error = error
        }) { }
}

public class NotFoundError : BaseError
{
    public NotFoundError(ErrorObject error)
        : base(404, new ErrorContext
        {
            Title = "Not Found Error",
            TitleKey = "server.errors.missingResource",
            Error = error
        }) { }
}

public class BadRequestError : BaseError
{
    public BadRequestError(ErrorObject error)
        : base(400, new ErrorContext
        {
            Title = "Bad Request Error",
            TitleKey = "server.errors.badRequest",
            Error = error
        }) { }
}

public class UnauthorizedError : BaseError
{
    public UnauthorizedError(ErrorObject error)
        : base(401, new ErrorContext
        {
            Title = "Unauthorized Error",
            TitleKey = "server.errors.unauthorized",
            Error = error
        }) { }
}

public class ForbiddenError : BaseError
{
    public ForbiddenError(ErrorObject error)
        : base(403, new ErrorContext
        {
            Title = "Forbidden Error",
            TitleKey = "server.errors.forbidden",
            Error = error
        }) { }
}

public class ConflictError : BaseError
{
    public ConflictError(ErrorObject error)
        : base(409, new ErrorContext
        {
            Title = "Conflict Error",
            TitleKey = "server.errors.conflict",
            Error = error
        }) { }
}

public class BusinessError : BaseError
{
    public BusinessError(ErrorObject error)
        : base(500, new ErrorContext
        {
            Title = "Business Error",
            TitleKey = "server.errors.business",
            Error = error
        }) { }
}

public class SystemError : BaseError
{
    public SystemError(ErrorObject error)
        : base(500, new ErrorContext
        {
            Title = "System Error",
            TitleKey = "server.errors.system",
            Error = error
        }) { }
}
