using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

/// A LIS link is one unit: "{UnitKey}:{ValuationKey}".
public class LisLinkKeyTests
{
    [Fact] public void Unit_and_valuation_key() =>
        Assert.Equal("378573767:10613673", LisLinkKey.Build(" 378573767 ", "10613673"));

    [Theory]
    [InlineData(null, "10613673")]
    [InlineData("378573767", null)]
    [InlineData("", "")]
    [InlineData("0", "10613673")]
    public void Missing_key_gives_null(string? unit, string? valuation) =>
        Assert.Null(LisLinkKey.Build(unit, valuation));
}
