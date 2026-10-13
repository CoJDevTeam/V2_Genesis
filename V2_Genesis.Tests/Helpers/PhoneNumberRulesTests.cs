using V2_Genesis.Helpers;
using Xunit;

namespace V2_Genesis.Tests.Helpers;

/// Cell numbers: any number starting with 0 (10 digits) or +27.
public class PhoneNumberRulesTests
{
    [Theory]
    [InlineData("0671234567")]
    [InlineData("067 123 4567")]
    [InlineData("082-000-0000")]
    [InlineData("0113456789")]
    [InlineData("+27671234567")]
    [InlineData("+27 67 123 4567")]
    [InlineData("+27 (0)67 123 4567")]
    [InlineData("+270671234567")]
    [InlineData("27671234567")]
    public void Accepted(string value) => Assert.True(PhoneNumberRules.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("671234567")]
    [InlineData("067123456")]
    [InlineData("06712345678")]
    [InlineData("+2767123456")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("+27 0 0 0000000")]
    [InlineData("067abc4567")]
    public void Rejected(string? value) => Assert.False(PhoneNumberRules.IsValid(value));
}
