using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class EnablePageNavigationTests
{
    private class FilterWithoutPageNav : VQueryFilter<TestProduct>
    {
        public FilterWithoutPageNav() { AllowAll(); }
    }

    private class FilterWithPageNav : VQueryFilter<TestProduct>
    {
        public FilterWithPageNav()
        {
            AllowAll();
            EnablePageNavigation();
        }
    }

    [Fact]
    public void Default_PageNavigationEnabled_IsFalse()
    {
        IVQueryFilter filter = new FilterWithoutPageNav();
        Assert.False(filter.PageNavigationEnabled);
    }

    [Fact]
    public void EnablePageNavigation_SetsFlag()
    {
        IVQueryFilter filter = new FilterWithPageNav();
        Assert.True(filter.PageNavigationEnabled);
    }
}
