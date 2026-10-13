using GenesisV2.Services.PropertySearch;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

public class LeaseLookupTests
{
    [Fact]
    public void Sup4_uses_UNITTYPE_FL_or_SL()
    {
        var sql = LeaseLookup.Sup4.BuildSql();
        Assert.Contains("FROM dbo.[Sup4]", sql);
        Assert.Contains("CAST([UNITTYPE] AS NVARCHAR(10))", sql);
        Assert.Contains("IN (N'FL', N'SL')", sql);
    }

    [Fact]
    public void Lis_uses_Unit_Type()
    {
        var sql = LeaseLookup.Lis_2026_10_01.BuildSql();
        Assert.Contains("FROM dbo.[LIS_2026_10_01]", sql);
        Assert.Contains("CAST([Unit Type] AS NVARCHAR(10))", sql);
        Assert.Contains("[Unit Key]", sql);
    }
}
