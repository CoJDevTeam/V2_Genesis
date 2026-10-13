using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

public class LeaseFlagsTests
{
    private static readonly List<LeaseFlag> Flags = new()
    {
        new LeaseFlag { UnitKey = 1969280, ValuationKey = 10613325 },                 // erf 2893 (R 0)
        new LeaseFlag { UnitKey = 1969281, ValuationKey = 10613326, IsLease = true }, // FL lease on erf 2893
    };

    [Theory]
    [InlineData("1969281", "10613326", "Y")]
    [InlineData("1969280", "10613325", "N")]
    [InlineData("1969281.0", "1.0613326E+07", "Y")]
    public void Search_column_is_Y_or_N(string unit, string val, string expected)
        => Assert.Equal(expected, LeaseFlags.SearchText(LeaseFlags.Find(Flags, unit, val)!));

    [Fact]
    public void Detail_is_Yes_or_No()
    {
        Assert.Equal("Yes", LeaseFlags.DetailText(Flags[1]));
        Assert.Equal("No", LeaseFlags.DetailText(Flags[0]));
    }

    [Fact]
    public void Unit_alone_is_used_when_the_valuation_does_not_match()
        => Assert.True(LeaseFlags.Find(Flags, "1969281", "999")!.IsLease);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("42")]
    public void Unknown_unit_has_no_flag(string? unit)
        => Assert.Null(LeaseFlags.Find(Flags, unit, "1"));
}
