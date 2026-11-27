using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Models;
using DiscordBot.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>Gangs: old saves moving into the roster, packs, the crew and defenders, fusing and scrapping.</summary>
    public class GangTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void OldSaves_MoveTheirOneCardIntoTheGang_AndKeepItsLevel()
        {
            string json = "{\"Accounts\":[{\"UserId\":5,\"Name\":\"old\",\"RaidStance\":2,\"RaidProtectedUntilUtcTicks\":5," +
                "\"BattlePerson\":{\"Name\":\"Gary\",\"Level\":12,\"Xp\":7,\"Rarity\":2,\"Ability\":4,\"Foil\":true}}]," +
                "\"CarriedCards\":{\"9\":{\"Name\":\"Kept\",\"Rarity\":3}}}";
            var data = JsonConvert.DeserializeObject<CoinAccounts>(json);
            var account = data.Accounts.Single();
            var card = Assert.Single(account.Cards);
            Assert.Equal("Gary", card.Name);
            Assert.Equal(12, card.Level);
            Assert.Equal(CardRarity.Epic, card.Rarity);
            Assert.False(string.IsNullOrEmpty(card.Id));
            Assert.Null(account.LegacyCard);
            Assert.Equal("Kept", Assert.Single(data.CarriedGangs[9]).Name);
            Assert.Empty(data.CarriedCards);

            //and they save in the new shape only, and load back the same
            string saved = JsonConvert.SerializeObject(data);
            Assert.DoesNotContain("\"BattlePerson\"", saved);
            Assert.DoesNotContain("CarriedCards", saved);
            var again = JsonConvert.DeserializeObject<CoinAccounts>(saved);
            Assert.Equal(card.Id, Assert.Single(again.Accounts.Single().Cards).Id);
            Assert.Single(again.CarriedGangs[9]);
        }

        [Fact]
        public void Packs_HaveTheirSize_AndKeepTheirGuarantee()
        {
            var random = new Random(3);
            foreach (var pack in GangRules.Packs)
                for (int i = 0; i < 300; i++)
                {
                    var cards = GangRules.Open(pack, random);
                    Assert.Equal(pack.Cards, cards.Count);
                    Assert.Contains(cards, c => c.Rarity >= pack.Guaranteed);
                    Assert.Equal(cards.Count, cards.Select(c => c.Id).Distinct().Count());
                }
        }

        [Fact]
        public void Kingpin_DoublesTheLegendaryChance()
        {
            var random = new Random(7);
            int plain = Enumerable.Range(0, 20_000).Count(_ => CardTraits.RollRarity(random) == CardRarity.Legendary);
            int boosted = Enumerable.Range(0, 20_000).Count(_ => CardTraits.RollRarity(random, 2) == CardRarity.Legendary);
            Assert.InRange(plain / 20_000.0, 0.025, 0.035);
            Assert.InRange(boosted / 20_000.0, 0.05, 0.07);
        }

        [Fact]
        public void FindPack_ReadsNamesAndFirstLetters()
        {
            Assert.Equal(PackKind.Kingpin, GangRules.FindPack("kingpin").Kind);
            Assert.Equal(PackKind.Crew, GangRules.FindPack("C").Kind);
            Assert.Equal(PackKind.Street, GangRules.FindPack("street").Kind);
            Assert.Null(GangRules.FindPack("mega"));
        }

        [Fact]
        public void TheCrew_IsTheBestRestedCards_AndEveryoneBestDefends()
        {
            var cards = new List<BattlePerson>
            {
                RaidTests.AverageCard(1), RaidTests.AverageCard(30), RaidTests.AverageCard(20), RaidTests.AverageCard(10), RaidTests.AverageCard(5),
            };
            cards[1].RestedUntilUtcTicks = Now.AddMinutes(30).Ticks;
            Assert.Equal(new[] { 20, 10, 5 }, GangRules.Crew(cards, Now).Select(c => c.Level));
            Assert.Equal(new[] { 30, 20, 10 }, GangRules.Defenders(cards).Select(c => c.Level));
            Assert.Equal(30, GangRules.At(cards, 1).Level);
            Assert.Throws<BadInputException>(() => GangRules.At(cards, 6));
        }

        [Fact]
        public void Fuse_TurnsThreeWeakestIntoOneOfTheNextRarity()
        {
            var cards = new List<BattlePerson>
            {
                RaidTests.AverageCard(9), RaidTests.AverageCard(2), RaidTests.AverageCard(4, foil: true), RaidTests.AverageCard(1), RaidTests.AverageCard(0, CardRarity.Rare),
            };
            var (used, made) = GangRules.Fuse(cards, CardRarity.Common, new Random(1));
            Assert.Equal(new[] { 1, 2, 4 }, used.Select(c => c.Level).OrderBy(l => l));
            Assert.Equal(CardRarity.Rare, made.Rarity);
            Assert.Equal(1, made.Level); //the lowest of the three, so fusing can't carry a trained card up
            Assert.True(made.Foil);
            Assert.Equal(3, cards.Count);
            Assert.Contains(made, cards);

            Assert.Throws<BadInputException>(() => GangRules.Fuse(cards, CardRarity.Common, new Random(1))); //only one common left
            Assert.Throws<BadInputException>(() => GangRules.Fuse(cards, CardRarity.Legendary, new Random(1)));
        }

        [Fact]
        public void Scrap_GivesRarityXpPlusSomeForLevels()
        {
            Assert.Equal(GangRules.ScrapXpByRarity[0], GangRules.ScrapXp(RaidTests.AverageCard(0)));
            Assert.Equal(GangRules.ScrapXpByRarity[3] + 10 * GangRules.ScrapXpPerLevel, GangRules.ScrapXp(RaidTests.AverageCard(10, CardRarity.Legendary)));
        }

        [Fact]
        public void Scrapping_NeverBeatsTraining()
        {
            //what a scrapped card gives on average, against what `.train` charges per XP at its cheapest (level 0)
            double averageXp = CardTraits.LegendaryChance * GangRules.ScrapXpByRarity[3] + CardTraits.EpicChance * GangRules.ScrapXpByRarity[2]
                + CardTraits.RareChance * GangRules.ScrapXpByRarity[1] + (1 - CardTraits.LegendaryChance - CardTraits.EpicChance - CardTraits.RareChance) * GangRules.ScrapXpByRarity[0];
            double cheapestTraining = CardLevels.FullLevelCost(0) / CardLevels.XpToNext(0);
            foreach (var pack in GangRules.Packs)
            {
                double xp = pack.Cards * averageXp * (pack.LegendaryBoost > 1 ? 1.5 : 1.2) + CardLevels.PullXp; //guarantees lift it a bit
                Assert.True(pack.Price / xp > cheapestTraining, $"a {pack.Name} scrapped buys XP at ${pack.Price / xp:0.##} each");
            }
            //and scrapping a trained card gives back far less than its training cost
            Assert.True(GangRules.ScrapXpPerLevel * 2 < CardLevels.XpToNext(0)); //under half of even the cheapest level
        }

        [Fact]
        public void TryParseRarity_ReadsWordsAndPlurals()
        {
            Assert.True(GangRules.TryParseRarity("commons", out var common));
            Assert.Equal(CardRarity.Common, common);
            Assert.True(GangRules.TryParseRarity("e", out var epic));
            Assert.Equal(CardRarity.Epic, epic);
            Assert.False(GangRules.TryParseRarity("shiny", out _));
        }

        [Fact]
        public void Glitched_IsTheStrongest_AndCantBeRolledOrFused()
        {
            Assert.True(RaidTests.AverageCard(0, CardRarity.Glitched).Power > RaidTests.AverageCard(0, CardRarity.Legendary).Power);
            var random = new Random(5);
            Assert.DoesNotContain(Enumerable.Range(0, 20_000).Select(_ => CardTraits.RollRarity(random, 2)), r => r == CardRarity.Glitched);
            var cards = Enumerable.Range(0, 3).Select(_ => RaidTests.AverageCard(0, CardRarity.Legendary)).ToList();
            Assert.Throws<BadInputException>(() => GangRules.Fuse(cards, CardRarity.Legendary, random));
            Assert.True(GangRules.ScrapXp(RaidTests.AverageCard(0, CardRarity.Glitched)) > GangRules.ScrapXp(RaidTests.AverageCard(0, CardRarity.Legendary)));
        }

        private static CoinAccount Player(ulong id) => new CoinAccount { UserId = id, Name = $"p{id}", LastActiveUtcTicks = Now.AddHours(-1).Ticks };

        [Fact]
        public void GlitchVotes_NeedThreeOthers_OneOfThemABotAdmin()
        {
            var owner = Player(1);
            var card = RaidTests.AverageCard(4);
            owner.Cards.Add(card);
            bool IsAdmin(ulong id) => id == 99;

            Assert.Throws<BadInputException>(() => GangRules.VoteGlitch(card, owner, owner, IsAdmin, Now)); //not your own
            Assert.False(GangRules.VoteGlitch(card, owner, Player(2), IsAdmin, Now));
            var three = Player(3);
            Assert.False(GangRules.VoteGlitch(card, owner, three, IsAdmin, Now));
            Assert.Throws<BadInputException>(() => GangRules.VoteGlitch(card, owner, three, IsAdmin, Now)); //once each
            var idle = Player(5);
            idle.LastActiveUtcTicks = Now.AddDays(-3).Ticks;
            Assert.Throws<BadInputException>(() => GangRules.VoteGlitch(card, owner, idle, IsAdmin, Now)); //not active
            Assert.False(GangRules.VoteGlitch(card, owner, Player(4), IsAdmin, Now)); //3 votes, no admin yet
            Assert.Contains("waiting for a bot admin", GangRules.VoteCount(card, IsAdmin));
            Assert.Equal(CardRarity.Common, card.Rarity);

            Assert.True(GangRules.VoteGlitch(card, owner, Player(99), IsAdmin, Now)); //the admin confirms
            Assert.Equal(CardRarity.Glitched, card.Rarity);
            Assert.Equal(4, card.Level);
            Assert.Throws<BadInputException>(() => GangRules.VoteGlitch(card, owner, Player(6), IsAdmin, Now)); //already Glitched
        }

        [Fact]
        public void AnAdmin_CanVoteTheirOwnCard()
        {
            var admin = Player(99);
            var card = RaidTests.AverageCard(0);
            admin.Cards.Add(card);
            bool IsAdmin(ulong id) => id == 99;
            Assert.False(GangRules.VoteGlitch(card, admin, admin, IsAdmin, Now));
            Assert.False(GangRules.VoteGlitch(card, admin, Player(2), IsAdmin, Now));
            Assert.True(GangRules.VoteGlitch(card, admin, Player(3), IsAdmin, Now));
            Assert.Equal(CardRarity.Glitched, card.Rarity);
        }

        [Fact]
        public void GlitchVotes_AdminFirst_UpgradesOnTheThirdVote()
        {
            var owner = Player(1);
            var card = RaidTests.AverageCard(0);
            owner.Cards.Add(card);
            bool IsAdmin(ulong id) => id == 99;
            Assert.False(GangRules.VoteGlitch(card, owner, Player(99), IsAdmin, Now));
            Assert.False(GangRules.VoteGlitch(card, owner, Player(2), IsAdmin, Now));
            Assert.True(GangRules.VoteGlitch(card, owner, Player(3), IsAdmin, Now));
        }

        [Fact]
        public void GlitchVotes_AreCappedPerDay()
        {
            var voter = Player(2);
            for (int i = 0; i < GangRules.GlitchVotesPerDay; i++)
            {
                var owner = Player(10 + (ulong)i);
                owner.Cards.Add(RaidTests.AverageCard(0));
                GangRules.VoteGlitch(owner.Cards[0], owner, voter, _ => false, Now);
            }
            var last = Player(50);
            last.Cards.Add(RaidTests.AverageCard(0));
            Assert.Throws<BadInputException>(() => GangRules.VoteGlitch(last.Cards[0], last, voter, _ => false, Now));
            GangRules.VoteGlitch(last.Cards[0], last, voter, _ => false, Now.AddDays(1).AddHours(-1).Date.AddHours(1)); //a new day
        }

        [Fact]
        public void APack_IsRefusedPastFifteenCards()
        {
            Assert.Equal(15, GangRules.MaxCards);
            var account = new CoinAccount { UserId = 1, Name = "a", NetWorth = 1e9 };
            account.Cards.AddRange(Enumerable.Range(0, 12).Select(_ => RaidTests.AverageCard(0)));
            DiscordBot.Managers.CommandManager.PackRefusal(account, GangRules.Pack(PackKind.Street), "20261127"); //12 + 3 fits
            Assert.Throws<BadInputException>(() => DiscordBot.Managers.CommandManager.PackRefusal(account, GangRules.Pack(PackKind.Crew), "20261127"));
            account.Cards.AddRange(Enumerable.Range(0, 10).Select(_ => RaidTests.AverageCard(0))); //an old gang of 22 keeps its cards
            Assert.Throws<BadInputException>(() => DiscordBot.Managers.CommandManager.PackRefusal(account, GangRules.Pack(PackKind.Street), "20261127"));
        }

        [Fact]
        public void TheGangPicture_DrawsAFullGangNumbered()
        {
            var cards = Enumerable.Range(0, GangRules.MaxCards).Select(i => RaidTests.AverageCard(i, (CardRarity)(i % 5), foil: i % 4 == 0)).ToList();
            using var picture = RaidRenderer.RenderGrid(cards, 5, numbered: true);
            using var image = SixLabors.ImageSharp.Image.Load(picture);
            Assert.True(image.Width < 900 && image.Height < 800);
            using var one = RaidRenderer.RenderGrid(cards.Take(1).ToList(), 5, numbered: true);
            Assert.True(one.Length > 0);
        }

        [Fact]
        public void PackPicture_Draws()
        {
            var cards = GangRules.Open(GangRules.Pack(PackKind.Crew), new Random(2));
            using var picture = RaidRenderer.RenderStrip(cards);
            Assert.True(picture.Length > 0);
        }
    }
}
