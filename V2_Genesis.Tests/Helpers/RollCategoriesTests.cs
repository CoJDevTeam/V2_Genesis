using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

/// The 17 categories of the Rates Policy 2026/27; Farming is Agricultural.
public class RollCategoriesTests
{
    [Fact]
    public void Seventeen_categories_with_Agricultural_and_no_Farming()
    {
        Assert.Equal(17, RollCategories.All.Count);
        Assert.Contains("Agricultural", RollCategories.All);
        Assert.DoesNotContain("Farming", RollCategories.All);
        Assert.Equal(RollCategories.All.Count, RollCategories.All.Distinct().Count());
    }

    [Theory]
    [InlineData("Farming", "Agricultural")]
    [InlineData("MultiPurpose*", "Multiple Purposes")]
    [InlineData("Multiple Purposes", "Multiple Purposes")]
    [InlineData("Public Service Infrastructure - Private", "Private Service Infrastructure")]
    [InlineData("Residential Property", "Residential")]
    [InlineData("Mining Land", "Mining")]
    [InlineData("vacant land", "Vacant Land")]
    public void Old_and_roll_names_map_to_the_list(string input, string expected) =>
        Assert.Equal(expected, RollCategories.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Something else")]
    public void Unknown_is_null(string? input) => Assert.Null(RollCategories.Normalize(input));
}
