using DiscordBot.Managers;
using DiscordBot.Models;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>`.portfolio` lists property by type instead of lumping it into "houses".</summary>
    public class PortfolioPropertyTests
    {
        [Fact]
        public void PropertyLines_ListEachTypeOwned_HousesThenBusinesses()
        {
            var account = new CoinAccount { UserId = 1, Name = "a" };
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.Cafe, PurchaseCost = 1_300_000 });
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House, PurchaseCost = 992_750 });
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House, PurchaseCost = 992_750 });
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.Luxury, PurchaseCost = 4_180_000 });
            double value = account.Properties.Sum(p => PropertyCatalog.SaleValue(p.Kind, 1.0));

            var lines = CommandManager.PropertyLines(account, 1.0, value);

            Assert.Equal(4, lines.Count); //the total, then House, Luxury home and Café (catalog order: houses then businesses)
            Assert.Contains("Property", lines[0]);
            Assert.Contains("earning", lines[0]);
            Assert.Contains("2 Houses", lines[1]);
            Assert.Contains("1 Luxury home", lines[2]);
            Assert.Contains("1 Café", lines[3]);
            Assert.Contains("swings ±50%", lines[3]);
            Assert.DoesNotContain("swings", lines[1]);
            Assert.All(lines.Skip(1), l => Assert.Contains("if sold now", l));
        }
    }
}
