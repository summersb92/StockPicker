using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>Verifies canonical symbol form: trim, upper-case, dot→dash.</summary>
    public class SymbolNormalizerTests
    {
        [Fact]
        public void ToCanonical_TrimsUppercasesAndConvertsDotToDash()
        {
            Assert.Equal("BRK-B", SymbolNormalizer.ToCanonical(" brk.b "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ToCanonical_ReturnsEmpty_ForNullOrWhitespace(string? input)
        {
            Assert.Equal(string.Empty, SymbolNormalizer.ToCanonical(input));
        }

        [Fact]
        public void ToCanonical_LeavesPlainSymbolUppercased()
        {
            Assert.Equal("AAPL", SymbolNormalizer.ToCanonical("aapl"));
        }
    }
}
