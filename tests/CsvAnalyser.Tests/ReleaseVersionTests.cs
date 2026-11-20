using CsvAnalyser.Core.Updates;

namespace CsvAnalyser.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v5.5", "5.4.7", true)]
    [InlineData("v5.4.10", "5.4.9", true)] // numbers, not text: 10 > 9
    [InlineData("5.4.7", "5.4.7", false)]
    [InlineData("v5.4", "5.4.7", false)] // v5.4 = 5.4.0
    [InlineData("V6", "5.9.30", true)]
    [InlineData("v5.5.0-beta", "5.4.7", true)]
    [InlineData("nightly", "5.4.7", false)]
    [InlineData("v5.5", "", false)]
    public void IsNewer_ComparesNumerically(string tag, string current, bool expected) =>
        Assert.Equal(expected, ReleaseVersion.IsNewer(tag, current));
}
