using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.BattleArena;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class CardLevelTests
    {
        [Fact]
        public void Xp_LevelsUpCarriesOverAndStopsAtMax()
        {
            var card = RaidTests.AverageCard(0);
            string line = CardLevels.AddXp(card, 150);
            Assert.Equal(1, card.Level);
            Assert.Equal(50, card.Xp);
            Assert.Contains("level up", line);

            CardLevels.AddXp(card, 100_000);
            Assert.Equal(CardLevels.MaxLevel, card.Level);
            Assert.Equal(0, card.Xp);

            var old = new BattlePerson { Level = 75 }; //an old save from before the cap
            Assert.Equal(CardLevels.MaxLevel, old.Level);
        }

        [Fact]
        public void Training_CostsRiseThirtyPercentALevel_LessTheXpYouHave()
        {
            Assert.Equal(2_000, CardLevels.FullLevelCost(0));
            Assert.InRange(CardLevels.FullLevelCost(10), 27_000, 28_000);
            Assert.InRange(CardLevels.FullLevelCost(29), 4_000_000, 4_200_000);
            double all = Enumerable.Range(0, CardLevels.MaxLevel).Sum(CardLevels.FullLevelCost);
            Assert.InRange(all, 17_000_000, 18_000_000);

            var card = RaidTests.AverageCard(0);
            card.Xp = 50; //half way to level 1
            Assert.Equal(1_000, CardLevels.TrainCost(card));
        }

        [Fact]
        public void Train_PaysIntoTheJackpot_AndMaxBuysWhatCashCovers()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 1, Name = "trainer", NetWorth = 10_000, BattlePerson = RaidTests.AverageCard(0) };
            coins.GetAll().Accounts.Add(account);
            var raids = new RaidService(coins, a => a.NetWorth, new Random(1));
            double jackpot = coins.JackpotPool;

            var (levels, cost) = raids.Train(account, CardLevels.MaxLevel);

            Assert.Equal(3, levels); //2,000 + 2,600 + 3,380 = 7,980; the next is 4,394
            Assert.Equal(7_980, cost);
            Assert.Equal(10_000 - 7_980, account.NetWorth);
            Assert.Equal(jackpot + 7_980, coins.JackpotPool, 6);
            Assert.Throws<BadInputException>(() => raids.Train(account, 1));
        }

        [Fact]
        public void Focus_GrowsOneStatFasterAndTheOthersSlower()
        {
            var card = RaidTests.AverageCard(20);
            double attack = card.Attack, defense = card.Defense;
            card.Focus = CardFocus.Attack;
            Assert.Equal(15 + 1.5 * 1.5 * 20, card.Attack, 6);
            Assert.True(card.Defense < defense);
            Assert.True(card.Attack > attack);
            card.Focus = CardFocus.Balanced;
            Assert.Equal(attack, card.Attack, 6);
        }

        [Fact]
        public void NewCard_KeepsThePlayersLevel()
        {
            var old = RaidTests.AverageCard(12);
            old.Xp = 77;
            old.Focus = CardFocus.Crit;
            var pulled = new BattlePerson("new card");
            CardLevels.CarryOver(old, pulled);
            Assert.Equal(12, pulled.Level);
            Assert.Equal(77, pulled.Xp);
            Assert.Equal(CardFocus.Crit, pulled.Focus);
        }

        [Fact]
        public async System.Threading.Tasks.Task NewSeason_KeepsCardsAtLevelZero()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var card = RaidTests.AverageCard(25);
            card.Wins = 9;
            coins.GetAll().Accounts.Add(new CoinAccount { UserId = 42, Name = "veteran", NetWorth = 5_000_000, BattlePerson = card });

            await coins.ClearAll();
            var account = await coins.Get(42, "veteran");

            Assert.Equal(10_000, account.NetWorth);
            Assert.Same(card, account.BattlePerson);
            Assert.Equal(0, account.BattlePerson.Level);
            Assert.Equal(0, account.BattlePerson.Wins);
            Assert.Empty(coins.GetAll().CarriedCards);
        }
    }
}
