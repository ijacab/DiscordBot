using Common.Services;
using DiscordBot;
using DiscordBot.Managers;
using DiscordBot.Services;
using Xunit;

namespace Tests
{
    public class JackpotTests
    {
        [Fact]
        public void JackpotChance_IsThreePercent()
        {
            Assert.Equal(0.03, BetManager.JackpotWinChance);
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(0.0299, true)]
        [InlineData(0.03, false)]
        [InlineData(0.99, false)]
        public void Roll_WinsOnlyBelowTheChance(double roll, bool expected)
        {
            Assert.Equal(expected, BetManager.RollsJackpot(roll));
        }

        [Fact]
        public void Pool_CollectsTaxesAndIsEmptiedWhenTaken()
        {
            //no Update is called, so the real coin file is never written
            var service = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            double before = service.JackpotPool;

            service.AddToJackpot(20);
            service.AddToJackpot(-5); //ignored
            Assert.Equal(before + 20, service.JackpotPool, 6);

            Assert.Equal(before + 20, service.TakeJackpot(), 6);
            Assert.Equal(0, service.JackpotPool);
        }
    }
}
