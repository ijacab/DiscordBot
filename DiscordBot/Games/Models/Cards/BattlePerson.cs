using Newtonsoft.Json;
using System;
using System.Runtime.Serialization;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>
    /// A player's fighter: a face, a funny name, a rarity, an archetype and a level. Its strength is one number, <see cref="Power"/>:
    /// the base, plus its rarity, plus what the level adds. The level (and XP) belong to the player and carry over to a new card.
    /// </summary>
    public class BattlePerson
    {
        public const int PullCost = 1000;
        private static readonly Random _random = new Random(); //best practice to use static object for random, not an instance for each time you want to use it
        private int _level;

        public string Name { get; set; }
        public int Level { get => _level; set => _level = Math.Clamp(value, 0, CardLevels.MaxLevel); }
        /// <summary>XP towards the next level (<see cref="CardLevels.XpToNext"/>).</summary>
        public int Xp { get; set; }
        public string FilePath { get; set; }
        public CardRarity Rarity { get; set; } = CardRarity.Common;
        /// <summary>Saved as "Ability": the old ability slots became archetypes, keeping their numbers.</summary>
        [JsonProperty("Ability")]
        public Archetype Archetype { get; set; } = Archetype.None;
        public bool Foil { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }

        /// <summary>How strong the card is: base + rarity + level. Raids turn it into odds (<see cref="Models.RaidRules"/>).</summary>
        [JsonIgnore] public double Power => CardLevels.PowerOf(Rarity, Level);

        /// <summary>Used when loading a saved card: nothing is rolled, the saved values are read back.</summary>
        public BattlePerson()
        {
        }

        /// <summary>Pulls a new card with a random rarity, archetype and foil. The name is the one given.</summary>
        public BattlePerson(string name)
        {
            Name = name;
            Roll(_random);
        }

        private void Roll(Random random)
        {
            Rarity = CardTraits.RollRarity(random);
            Archetype = CardTraits.RollArchetype(random);
            Foil = CardTraits.RollFoil(random);
        }

        /// <summary>Pulls a new card with a random rarity, archetype and foil, named to match.</summary>
        public static BattlePerson Pull(Random random = null)
        {
            random ??= _random;
            var card = new BattlePerson();
            card.Roll(random);
            card.Name = CardNamer.Generate(card.Archetype, random);
            return card;
        }

        [OnDeserialized]
        private void FixOldSaves(StreamingContext context) => Archetype = Archetypes.Normalise(Archetype);

        public string GetSummary()
        {
            string foil = Foil ? "✨ **FOIL** (a rare shiny version)\n" : "";
            string level = Level >= CardLevels.MaxLevel ? "max level" : $"level {Level} ({Xp}/{CardLevels.XpToNext(Level)} XP)";
            return foil +
                   $"{Archetypes.Label(Archetype)} · **{Rarity}**\n" +
                   $"**Power {Power:0}** · {level} · W {Wins} / L {Losses}\n" +
                   Archetypes.Trait(Archetype);
        }
    }
}
