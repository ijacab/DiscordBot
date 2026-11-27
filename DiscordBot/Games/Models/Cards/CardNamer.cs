using System;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>Gang names for new cards: a first name, a nickname and a surname, like Gary "Knuckles" Pemberton.</summary>
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

        private static readonly string[] Nicknames =
        {
            "Knuckles", "The Hammer", "Tiny", "Meatloaf", "Bonecrusher", "Big Tuna", "Sledge", "Haymaker", "The Wall", "Brick", "Iron Nan",
            "Doorstop", "Fingers", "Sticky", "Pocket Rocket", "Sneaky", "Slippery", "The Weasel", "Gloves", "Five-Finger", "Smoke", "The Fox",
            "Jinx", "Shifty", "Sleeves", "Wildcard", "Snake Eyes", "Lucky", "All-In", "Ace", "Long Shot", "Crowbar", "Getaway", "The Accountant",
            "Two Dogs", "Mad Dog", "Bolt Cutter", "Ski Mask", "Lockpick", "The Cleaner",
        };

        public static string Generate(Random random) =>
            $"{First[random.Next(First.Length)]} \"{Nicknames[random.Next(Nicknames.Length)]}\" {Last[random.Next(Last.Length)]}";
    }
}
