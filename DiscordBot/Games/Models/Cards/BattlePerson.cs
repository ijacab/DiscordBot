using Newtonsoft.Json;
using System;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>
    /// One member of a player's gang: a face, a funny name, a rarity, maybe a foil, and its own level and XP. Its strength is one
    /// number, <see cref="Power"/>: the base, plus its rarity, plus what its level adds, and a foil adds <see cref="GangRules.FoilBonus"/>
    /// on top. A player holds up to <see cref="GangRules.MaxCards"/> of them (<see cref="Models.CoinAccounts.CoinAccount.Cards"/>).
    /// Old saves' "Ability" (the archetype cards had) is ignored when they load.
    /// </summary>
    public class BattlePerson
    {
        private static readonly Random _random = new Random();
        private int _level;

        /// <summary>Picks this card out of the roster for buttons, menus and `.scrap`. Old saves get one when they load.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public int Level { get => _level; set => _level = Math.Clamp(value, 0, CardLevels.MaxLevel); }
        /// <summary>XP towards the next level (<see cref="CardLevels.XpToNext"/>).</summary>
        public int Xp { get; set; }
        public string FilePath { get; set; }
        public CardRarity Rarity { get; set; } = CardRarity.Common;
        public bool Foil { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        /// <summary>After a raid the card lies low until then (UTC ticks) and can't be sent on another (<see cref="GangRules.RestTime"/>). Defending never tires it.</summary>
        public long RestedUntilUtcTicks { get; set; }
        /// <summary>Everyone who voted this card's face glitched (<see cref="GangRules.VoteGlitch"/>).</summary>
        public System.Collections.Generic.List<ulong> GlitchVoterIds { get; set; } = new System.Collections.Generic.List<ulong>();

        /// <summary>How strong the card is: base + rarity + level, then the foil bonus. Raids add up a crew's (<see cref="Models.RaidRules"/>).</summary>
        [JsonIgnore] public double Power => CardLevels.PowerOf(Rarity, Level) * (Foil ? 1 + GangRules.FoilBonus : 1);

        [JsonIgnore] public string Emoji => Foil ? "✨" : GangRules.RarityEmoji(Rarity);

        public bool IsResting(DateTime now) => RestedUntilUtcTicks > now.Ticks;

        /// <summary>Used when loading a saved card: nothing is rolled, the saved values are read back.</summary>
        public BattlePerson()
        {
        }

        /// <summary>
        /// A new card with a random rarity (at least <paramref name="minimum"/>, Legendary <paramref name="legendaryBoost"/> times as
        /// likely) and foil, and a random gang name.
        /// </summary>
        public static BattlePerson Pull(Random random = null, CardRarity minimum = CardRarity.Common, double legendaryBoost = 1)
        {
            random ??= _random;
            var rarity = CardTraits.RollRarity(random, legendaryBoost);
            return new BattlePerson
            {
                Rarity = rarity < minimum ? minimum : rarity,
                Foil = CardTraits.RollFoil(random),
                Name = CardNamer.Generate(random),
            };
        }

        /// <summary>"⭐ **Gary "Knuckles" Pemberton** · Rare · Lv 4 · power 47".</summary>
        public string Line() => $"{Emoji} **{Name}** · {(Foil ? "Foil " : "")}{Rarity} · Lv {Level} · power {Power:0}";

        public string GetSummary()
        {
            string foil = Foil ? $"✨ **FOIL**: +{GangRules.FoilBonus:0%} power\n" : "";
            string level = Level >= CardLevels.MaxLevel ? "max level" : $"level {Level} ({Xp}/{CardLevels.XpToNext(Level)} XP)";
            return foil +
                   $"{GangRules.RarityEmoji(Rarity)} **{Rarity}** · **Power {Power:0}** · {level}\n" +
                   $"Raids: W {Wins} / L {Losses}";
        }
    }
}
