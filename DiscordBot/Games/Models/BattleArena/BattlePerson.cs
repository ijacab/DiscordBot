using Newtonsoft.Json;
using System;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattlePerson
    {
        public const int PullCost = 1000;
        private static readonly Random _random = new Random(); //best practice to use static object for random, not an instance for each time you want to use it

        public string Name { get; set; }
        public int Level { get; set; }
        public string FilePath { get; set; }
        public CardRarity Rarity { get; set; } = CardRarity.Common;
        public CardAbility Ability { get; set; } = CardAbility.None;
        public BattleStats Stats { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }

        //battle stats, calculated from the saved stats and level
        [JsonIgnore] public double Attack => Stats.GetTotalAttack(Level);
        [JsonIgnore] public double Defense => Stats.GetTotalDefense(Level);
        [JsonIgnore] public double CritMultiplier => Stats.GetTotalCritMultiplier(Level);

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
            Stats = BattleStats.Generate(_random, Rarity.StatMultiplier());
        }

        public string GetSummary()
        {
            return $"**Rarity:** {Rarity}\n" +
                   $"**Ability:** {Ability} - {Ability.Describe()}\n" +
                   $"**Level:** {Level} (W {Wins} / L {Losses})\n" +
                   $"**Attack:** {Attack:0.0}\n" +
                   $"**Defense:** {Defense:0.0}\n" +
                   $"**Crit multiplier:** {CritMultiplier:0.00}x";
        }
    }
}
