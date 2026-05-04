using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class RetryOnConflictTests
{
    private static ConflictError MakeConflict() =>
        new(new ErrorObject { Message = "x", MessageKey = "y", Metadata = new { kind = "concurrent_update" } });

    [Fact]
    public async Task RetryOnConflict_OperationSucceedsFirstTime_ReturnsValue()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        int callCount = 0;

        var result = await db.RetryOnConflictAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            return "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryOnConflict_OperationConflictsThenSucceeds_RetriesAndReturns()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        int callCount = 0;

        var result = await db.RetryOnConflictAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount == 1) throw MakeConflict();
            return "ok-after-retry";
        });

        Assert.Equal("ok-after-retry", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryOnConflict_AlwaysConflicts_ThrowsAfterMaxAttempts()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        int callCount = 0;

        await Assert.ThrowsAsync<ConflictError>(async () =>
        {
            await db.RetryOnConflictAsync<string>(async () =>
            {
                callCount++;
                await Task.CompletedTask;
                throw MakeConflict();
            }, maxAttempts: 3);
        });

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryOnConflict_CallsOnRetryCallback_ForEachRetry()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        int callCount = 0;
        var retryCalls = new List<int>();

        await db.RetryOnConflictAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount < 3) throw MakeConflict();
        }, maxAttempts: 3, onRetry: (attempt, _) => retryCalls.Add(attempt));

        Assert.Equal([1, 2], retryCalls);
    }
}
