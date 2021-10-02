using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Tests
{
    public class BlackjackTests
    {
        [Fact]
        public void CardTotalPossibleValues_ShouldReturn_AsExpected()
        {
            var player = new BlackjackPlayer();
            player.Cards = new List<Card> {
                new Card("A", Suit.Hearts),
                new Card("A", Suit.Hearts),
                new Card("10", Suit.Hearts)
            };

            var values = player.GetPossibleTotalValues(false).OrderBy(v => v).ToArray();
            Assert.True(values.Count() == 3);
            Assert.True(values[0] == 12);
            Assert.True(values[1] == 22);
            Assert.True(values[2] == 32);

            player.Cards = new List<Card> {
                new Card("K", Suit.Hearts),
                new Card("A", Suit.Hearts)
            };

            values = player.GetPossibleTotalValues(false).OrderBy(v => v).ToArray();
            Assert.True(values.Count() == 2);
            Assert.True(values[0] == 11);
            Assert.True(values[1] == 21);


            player.Cards = new List<Card> {
                new Card("2", Suit.Hearts),
                new Card("J", Suit.Hearts),
                new Card("Q", Suit.Hearts),
                new Card("K", Suit.Hearts)
            };

            values = player.GetPossibleTotalValues(false).OrderBy(v => v).ToArray();
            Assert.True(values.Count() == 1);
            Assert.True(values[0] == 32);


            player.Cards = new List<Card> {
                new Card("2", Suit.Hearts),
                new Card("A", Suit.Hearts),
                new Card("K", Suit.Hearts),
                new Card("A", Suit.Hearts)
            };

            values = player.GetPossibleTotalValues(false).OrderBy(v => v).ToArray();
            Assert.True(values.Count() == 3);
            Assert.True(values[0] == 14);
            Assert.True(values[1] == 24);
            Assert.True(values[2] == 34);

        }
    }
}
