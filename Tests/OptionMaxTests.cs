using DiscordBot.Models;
using Xunit;

namespace Tests
{
    public class OptionMaxTests
    {
        [Fact]
        public void MaxAffordableContracts_BuysAsManyAsPremiumPlusFeeAllows()
        {
            //$3 a share is $300 a contract, plus a 2% fee, so $306 each
            Assert.Equal(3, Stock.MaxAffordableContracts(1_000, 3));
            Assert.Equal(3, Stock.MaxAffordableContracts(918, 3));
            Assert.Equal(2, Stock.MaxAffordableContracts(917.99, 3));
        }

        [Fact]
        public void MaxAffordableContracts_IsZeroWhenNotEvenOneIsAffordable()
        {
            Assert.Equal(0, Stock.MaxAffordableContracts(100, 3));
            Assert.Equal(0, Stock.MaxAffordableContracts(0, 3));
            Assert.Equal(0, Stock.MaxAffordableContracts(-50, 3));
        }

        [Fact]
        public void MaxAffordableContracts_NeverExceedsTheCash()
        {
            for (double cash = 1; cash < 5_000; cash += 37.3)
            {
                int n = Stock.MaxAffordableContracts(cash, 1.7);
                Assert.True(Stock.OptionCost(1.7, n) <= cash, $"{n} contracts cost more than ${cash:N2}");
                Assert.True(Stock.OptionCost(1.7, n + 1) > cash, $"{n + 1} contracts would have fit in ${cash:N2}");
            }
        }
    }
}
