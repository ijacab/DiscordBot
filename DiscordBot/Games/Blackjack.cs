using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games
{
    public class Blackjack
    {
        public int Id { get; set; }
        private readonly CardDeck _deck;
        public Blackjack()
        {
            _deck = new CardDeck(6);
        }
        public void Hit(BlackjackPlayer player)
        {
            var card = _deck.Take();
        }
    }
}
