using Newtonsoft.Json;
using System;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattlePerson
    {
        public const int PullCost = 1000;
        private static readonly Random _random = new Random(); //best practice to use static object for random, not an instance for each time you want to use it

        private int _level;

        public string Name { get; set; }
        /// <summary>The player's level, 0 to <see cref="CardLevels.MaxLevel"/>. It belongs to the player: keeping a new card carries it over.</summary>
        public int Level { get => _level; set => _level = Math.Clamp(value, 0, CardLevels.MaxLevel); }
        /// <summary>XP towards the next level (<see cref="CardLevels.XpToNext"/>).</summary>
        public int Xp { get; set; }
        /// <summary>Which stat grows fastest as the card levels. Balanced keeps the card's own growth.</summary>
        public CardFocus Focus { get; set; } = CardFocus.Balanced;
        public string FilePath { get; set; }
        public CardRarity Rarity { get; set; } = CardRarity.Common;
        public CardAbility Ability { get; set; } = CardAbility.None;
        public bool Foil { get; set; }
        public BattleStats Stats { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }

        //battle stats, calculated from the saved stats and level
        [JsonIgnore] public double Attack => Stats.GetTotalAttack(Level, CardLevels.Growth(Focus, CardFocus.Attack));
        [JsonIgnore] public double Defense => Stats.GetTotalDefense(Level, CardLevels.Growth(Focus, CardFocus.Defense));
        [JsonIgnore] public double CritMultiplier => Stats.GetTotalCritMultiplier(Level, CardLevels.Growth(Focus, CardFocus.Crit));

        /// <summary>Used when loading a saved card. The stats come from the saved JSON, so nothing is re-rolled.</summary>
        public BattlePerson()
        {
            Stats = BattleStats.Generate(_random, 1); //only kept when the saved card has no stats
        }

        /// <summary>Pulls a new card with random rarity, ability and stats.</summary>
        public BattlePerson(string name) : this()
        {
            Name = name;
            Rarity = CardTraits.RollRarity(_random);
            Ability = CardTraits.RollAbility(_random);
            Foil = CardTraits.RollFoil(_random);
            Stats = BattleStats.Generate(_random, Rarity.StatMultiplier());
        }

        public string GetSummary()
        {
            string foilLine = Foil ? "**FOIL** (rare variant)\n" : "";
            return foilLine +
                   $"**Rarity:** {Rarity}\n" +
                   $"**Ability:** {Ability} - {Ability.Describe()}\n" +
                   $"**Level:** {Level}{(Level >= CardLevels.MaxLevel ? " (max)" : $" ({Xp}/{CardLevels.XpToNext(Level)} XP)")}, W {Wins} / L {Losses}\n" +
                   (Focus != CardFocus.Balanced ? $"**Focus:** {Focus}\n" : "") +
                   $"**Attack:** {Attack:0.0}\n" +
                   $"**Defense:** {Defense:0.0}\n" +
                   $"**Crit multiplier:** {CritMultiplier:0.00}x";
        }
    }
}
