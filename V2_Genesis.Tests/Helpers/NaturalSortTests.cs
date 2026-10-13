using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

/// Townships in natural order: EXT.9 before EXT.49.
public class NaturalSortTests
{
    [Fact]
    public void Extensions_in_number_order()
    {
        var input = new[] { "ALLANDALE EXT.9", "AMOROSA EXT.30", "ALLANDALE EXT.49", "ALLANDALE 10-IR",
                            "AMOROSA EXT.10", "alexandra ext.3", "ALEXANDRA EXT.51", "AMOROSA EXT.11", "", null };
        Assert.Equal(
            new[] { "alexandra ext.3", "ALEXANDRA EXT.51", "ALLANDALE 10-IR", "ALLANDALE EXT.9",
                    "ALLANDALE EXT.49", "AMOROSA EXT.10", "AMOROSA EXT.11", "AMOROSA EXT.30" },
            NaturalSort.Order(input));
    }

    [Fact]
    public void Duplicates_removed() =>
        Assert.Single(NaturalSort.Order(new[] { "GRESSWOLD", "Gresswold " }));

    [Fact]
    public void Categories_are_a_to_z() =>
        Assert.Equal(RollCategories.All.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), RollCategories.All);
}
