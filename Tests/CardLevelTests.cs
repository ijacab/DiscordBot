using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
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
            var card = RaidTests.AverageCard(0);
            var account = new CoinAccount { UserId = 1, Name = "trainer", NetWorth = 10_000, Cards = { card } };
            coins.GetAll().Accounts.Add(account);
            var raids = new RaidService(coins, a => a.NetWorth, new Random(1));
            double jackpot = coins.JackpotPool;

            var (levels, cost) = raids.Train(account, card, CardLevels.MaxLevel);

            Assert.Equal(3, levels); //2,000 + 2,600 + 3,380 = 7,980; the next is 4,394
            Assert.Equal(7_980, cost);
            Assert.Equal(10_000 - 7_980, account.NetWorth);
            Assert.Equal(jackpot + 7_980, coins.JackpotPool, 6);
            Assert.Throws<BadInputException>(() => raids.Train(account, card, 1));
            Assert.Throws<BadInputException>(() => raids.Train(account, RaidTests.AverageCard(0), 1)); //not theirs
        }

        [Fact]
        public void Power_IsBasePlusRarityPlusLevels()
        {
            Assert.Equal(CardLevels.BasePower + CardLevels.RarityPower(CardRarity.Common), CardLevels.PowerOf(CardRarity.Common, 0));
            Assert.Equal(CardLevels.PowerOf(CardRarity.Epic, 0) + 10 * CardLevels.PowerPerLevel, CardLevels.PowerOf(CardRarity.Epic, 10), 6);
            Assert.Equal(CardLevels.PowerOf(CardRarity.Common, CardLevels.MaxLevel), CardLevels.PowerOf(CardRarity.Common, 99)); //capped at max level

            var card = RaidTests.AverageCard(7, rarity: CardRarity.Rare);
            Assert.Equal(CardLevels.PowerOf(CardRarity.Rare, 7), card.Power, 6);
            double[] byRarity = new[] { CardRarity.Common, CardRarity.Rare, CardRarity.Epic, CardRarity.Legendary }.Select(r => CardLevels.PowerOf(r, 0)).ToArray();
            Assert.Equal(byRarity.OrderBy(x => x), byRarity); //a better rarity is always stronger
        }

        [Fact]
        public void ARarity_IsWorthAboutTheTrainingItWouldTake()
        {
            //a legendary starts 13 power ahead of a common one, about 7 levels, which cost about $37,000 to train: so finding one is worth tens of thousands, and a street pack's cards ($1,000 each, 3% legendary) cost about that
            double levelsAhead = (CardLevels.RarityPower(CardRarity.Legendary) - CardLevels.RarityPower(CardRarity.Common)) / CardLevels.PowerPerLevel;
            double trainingCost = Enumerable.Range(0, (int)Math.Round(levelsAhead)).Sum(CardLevels.FullLevelCost);
            Assert.InRange(trainingCost, 25_000, 50_000);
            var street = GangRules.Pack(PackKind.Street);
            Assert.InRange(street.Price / street.Cards / CardTraits.LegendaryChance, 25_000, 50_000);
        }

        [Fact]
        public async System.Threading.Tasks.Task NewSeason_KeepsTheWholeGangAtLevelZero()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var card = RaidTests.AverageCard(25);
            card.Wins = 9;
            card.RestedUntilUtcTicks = DateTime.UtcNow.AddHours(1).Ticks;
            var other = RaidTests.AverageCard(3, CardRarity.Epic);
            coins.GetAll().Accounts.Add(new CoinAccount { UserId = 42, Name = "veteran", NetWorth = 5_000_000, Cards = { card, other } });

            await coins.ClearAll();
            var account = await coins.Get(42, "veteran");

            Assert.Equal(10_000, account.NetWorth);
            Assert.Equal(2, account.Cards.Count);
            Assert.Contains(card, account.Cards);
            Assert.All(account.Cards, c => Assert.Equal(0, c.Level));
            Assert.Equal(0, card.Wins);
            Assert.Equal(0, card.RestedUntilUtcTicks);
            Assert.Empty(coins.GetAll().CarriedGangs);
        }
    }
}
