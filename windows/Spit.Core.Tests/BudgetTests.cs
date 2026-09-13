namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/BudgetTests.swift.
public sealed class BudgetTests
{
    [Fact]
    public void testClampAndScale()
    {
        Assert.Equal(3500, Budget.Ms(rawChars: 0));
        Assert.Equal(3700, Budget.Ms(rawChars: 100));
        Assert.Equal(15000, Budget.Ms(rawChars: 2000));
    }
}
