namespace VAppCore.Tests;

public class AuditSuppressionTests
{
    [Fact]
    public void IsSuppressed_DefaultsFalse()
    {
        Assert.False(AuditSuppression.IsSuppressed);
    }

    [Fact]
    public void Suppress_SetsIsSuppressedTrueWithinScope()
    {
        using (AuditSuppression.Suppress())
        {
            Assert.True(AuditSuppression.IsSuppressed);
        }
        Assert.False(AuditSuppression.IsSuppressed);
    }

    [Fact]
    public void Suppress_NestedScopesUseDepth()
    {
        using (AuditSuppression.Suppress())
        {
            Assert.True(AuditSuppression.IsSuppressed);
            using (AuditSuppression.Suppress())
            {
                Assert.True(AuditSuppression.IsSuppressed);
            }
            Assert.True(AuditSuppression.IsSuppressed); // outer still active
        }
        Assert.False(AuditSuppression.IsSuppressed);
    }

    [Fact]
    public async Task Suppress_FlowsAcrossAwaits()
    {
        using (AuditSuppression.Suppress())
        {
            await Task.Yield();
            Assert.True(AuditSuppression.IsSuppressed);
        }
    }

    [Fact]
    public async Task Suppress_DisposeClearsAfterAwait()
    {
        using (AuditSuppression.Suppress())
        {
            await Task.Yield();
        }
        Assert.False(AuditSuppression.IsSuppressed);
    }
}
