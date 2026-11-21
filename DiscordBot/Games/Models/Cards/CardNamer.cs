using System;
using System.Collections.Generic;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>Names for pulled cards: a first name, a nickname that fits the archetype, and a surname, like Gary "Knuckles" Pemberton.</summary>
    public static class CardNamer
    {
        private static readonly string[] First =
        {
            "Gary", "Brenda", "Dave", "Kevin", "Karen", "Pete", "Dorothy", "Barry", "Linda", "Trevor", "Sheila", "Bruce", "Maureen", "Colin",
            "Tiffany", "Derek", "Gladys", "Wayne", "Bernie", "Moira", "Craig", "Doreen", "Keith", "Shirley", "Jolene", "Terry", "Marge", "Lenny",
        };

        private static readonly string[] Last =
        {
            "Pemberton", "Okafor", "Nguyen", "McGee", "Kowalski", "Fitzgerald", "Tanaka", "Donnelly", "Hargreaves", "Vasquez", "Bloggs",
            "Fairweather", "Calloway", "Brannigan", "Dufresne", "Pickles", "Plunkett", "Snodgrass", "Wobblebottom", "Crumpet", "Haddock", "Thistlewood",
        };

        private static readonly Dictionary<Archetype, string[]> Nicknames = new Dictionary<Archetype, string[]>
        {
            [Archetype.Brute] = new[] { "Knuckles", "The Hammer", "Tiny", "Meatloaf", "Bonecrusher", "Big Tuna", "Sledge", "Haymaker" },
            [Archetype.Guardian] = new[] { "The Wall", "Fortress", "Bubble Wrap", "Brick", "Iron Nan", "Doorstop", "Bunker", "Tupperware" },
            [Archetype.Thief] = new[] { "Fingers", "Sticky", "Pocket Rocket", "Sneaky", "Slippery", "The Weasel", "Gloves", "Five-Finger" },
            [Archetype.Trickster] = new[] { "Smoke", "The Fox", "Jinx", "Shifty", "Mirage", "Sleeves", "Plot Twist", "Wildcard" },
            [Archetype.Gambler] = new[] { "Snake Eyes", "Lucky", "All-In", "Ace", "Double Down", "Jackpot", "Long Shot", "Cashout" },
            [Archetype.None] = new[] { "The Rookie", "Nobody" },
        };

        public static string Generate(Archetype archetype, Random random)
        {
            var nicks = Nicknames.TryGetValue(Archetypes.Normalise(archetype), out var found) ? found : Nicknames[Archetype.None];
            return $"{First[random.Next(First.Length)]} \"{nicks[random.Next(nicks.Length)]}\" {Last[random.Next(Last.Length)]}";
        }
    }
}
