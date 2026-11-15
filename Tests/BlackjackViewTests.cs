using DiscordBot.Games;
using DiscordBot.Games.Models;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Games.Blackjack;

namespace Tests
{
    /// <summary>The blackjack table that is edited in place: lobby, hands and results.</summary>
    public class BlackjackViewTests
    {
        private static Card C(string name, Suit suit = Suit.Spades) => new Card(name, suit);

        private static BlackjackPlayer Player(string name, double bet, params Card[] cards) =>
            new BlackjackPlayer { UserId = (ulong)name.GetHashCode() & 0xFFFF, Username = name, BetAmount = bet, Cards = cards.ToList() };

        private static Blackjack Game(Card[] dealer, params BlackjackPlayer[] players)
        {
            var game = new Blackjack();
            game.Join(players);
            game.GetDealer().Cards = dealer.ToList();
            return game;
        }

        [Fact]
        public void Totals_ShowSoftAndBust()
        {
            Assert.Equal("**soft 17**", BlackjackView.Total(Player("a", 1, C("A"), C("6"))));
            Assert.Equal("**17**", BlackjackView.Total(Player("a", 1, C("K"), C("7"))));
            Assert.Equal("**bust** (25)", BlackjackView.Total(Player("a", 1, C("K"), C("7"), C("8"))));
        }

        [Fact]
        public void Cards_AreChipsWithFilledSuits()
        {
            Assert.Equal("`A♥` `10♣`", BlackjackView.Hand(new[] { C("A", Suit.Hearts), C("10", Suit.Clubs) }));
        }

        [Fact]
        public void Lobby_ListsWhoSatDownAndHowToStart()
        {
            var game = Game(new Card[0], Player("alice", 1_000), Player("bob", 500));
            var embed = BlackjackView.Lobby(game);
            Assert.Contains("table open", embed.Title);
            Assert.Contains("**alice** · $1,000", embed.Description);
            Assert.Contains("**bob** · $500", embed.Description);
            Assert.Contains("**Start**", embed.Description);
        }

        [Fact]
        public void InPlay_HidesTheDealersSecondCardAndShowsEachStatus()
        {
            var deciding = Player("alice", 1_000, C("10"), C("6"));
            var stood = Player("bob", 500, C("K"), C("9"));
            stood.IsFinishedPlaying = true;
            var bust = Player("cat", 200, C("K"), C("9"), C("5"));
            bust.IsFinishedPlaying = true;
            var game = Game(new[] { C("Q", Suit.Diamonds) }, deciding, stood, bust);

            string text = BlackjackView.InPlay(game).Description;
            Assert.Contains("**Dealer** `Q♦` `??` · showing **10**", text);
            Assert.Contains("🎴 deciding", text);
            Assert.Contains("✋ stood", text);
            Assert.Contains("💥 bust", text);
            Assert.Equal(5, text.Trim().Split('\n').Length); //dealer, rule and one line per player
        }

        [Fact]
        public void Results_ShowEachOutcomeAndPayout()
        {
            var winner = Player("alice", 1_000, C("K"), C("9"));
            winner.BaseWinnings = 2_000;
            winner.BonusWinnings = 500;
            var loser = Player("bob", 500, C("K"), C("9"), C("5"));
            var game = Game(new[] { C("10"), C("7") }, winner, loser);

            var lines = new List<BlackjackView.ResultLine>
            {
                new BlackjackView.ResultLine { Player = winner, Result = game.ResultOf(winner), NetWorth = 12_000, Notes = new[] { "🎲 First gamble of the day!" } },
                new BlackjackView.ResultLine { Player = loser, Result = game.ResultOf(loser), NetWorth = 4_500 },
            };
            string text = BlackjackView.Results(game, lines).Description;

            Assert.Equal(BlackjackResultType.Win, game.ResultOf(winner));
            Assert.Contains("✅ **alice** won", text);
            Assert.Contains("$1,000 → $2,000 (+$500 bonus) · cash $12,000", text);
            Assert.Contains("🎲 First gamble of the day!", text);
            Assert.Contains("💥 **bob** bust", text);
            Assert.Contains("**Dealer** `10♠` `7♠` **17**", text);
        }
    }
}
