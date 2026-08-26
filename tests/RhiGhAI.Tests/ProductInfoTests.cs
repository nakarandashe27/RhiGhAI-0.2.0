using RhiGhAI.Core;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void MinimumHostMatchesConfirmedContract()
    {
        Assert.Equal(8, ProductInfo.MinimumRhinoMajor);
        Assert.Equal(20, ProductInfo.MinimumRhinoMinor);
    }
}
