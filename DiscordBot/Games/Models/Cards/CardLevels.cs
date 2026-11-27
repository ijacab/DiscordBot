using System;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>
    /// Card levels: each card levels on its own. XP comes from raids, packs and scrapping other cards, and `.train` buys the
    /// rest of the next level for cash (into the jackpot). Levels reset each season; the cards are kept.
    /// </summary>
    public static class CardLevels
    {
        public const int MaxLevel = 30;

        //XP for each event
        public const int RaidWinXp = 60;
        /// <summary>On top of <see cref="RaidWinXp"/> when the target was richer.</summary>
        public const int RaidUpBonusXp = 40;
        public const int DefenceWinXp = 40;
        public const int RaidLossXp = 15;
        public const int DefenceLossXp = 10;
        /// <summary>Every pack opened gives this much XP to your strongest card.</summary>
        public const int PullXp = 25;

        /// <summary>A whole level from 0 costs this; each level after costs <see cref="TrainGrowth"/> times the one before.</summary>
        public const double TrainBaseCost = 2000;
        public const double TrainGrowth = 1.3;

        /// <summary>The power every card starts from, then its rarity's, then each level's.</summary>
        public const double BasePower = 20;
        public const double PowerPerLevel = 1.8;

        public static double RarityPower(CardRarity rarity)
        {
            switch (rarity)
            {
                case CardRarity.Rare: return 14;
                case CardRarity.Epic: return 18;
                case CardRarity.Legendary: return 23;
                case CardRarity.Glitched: return 30;
                default: return 10;
            }
        }

        public static double PowerOf(CardRarity rarity, int level) => BasePower + RarityPower(rarity) + PowerPerLevel * Math.Clamp(level, 0, MaxLevel);

        /// <summary>XP needed to go from <paramref name="level"/> to the next.</summary>
        public static int XpToNext(int level) => 100 + 20 * level;

        /// <summary>Adds XP and levels up as far as it reaches. Returns a line for the result message.</summary>
        public static string AddXp(BattlePerson card, int xp)
        {
            if (card == null || xp <= 0) return "";
            if (card.Level >= MaxLevel)
            {
                card.Xp = 0;
                return $"{card.Name} is max level.";
            }

            int before = card.Level;
            card.Xp += xp;
            while (card.Level < MaxLevel && card.Xp >= XpToNext(card.Level))
            {
                card.Xp -= XpToNext(card.Level);
                card.Level++;
            }
            if (card.Level >= MaxLevel) card.Xp = 0;

            return card.Level > before
                ? $"+{xp} XP, ⭐ **level up! {before} → {card.Level}**"
                : $"+{xp} XP ({card.Xp}/{XpToNext(card.Level)})";
        }

        /// <summary>What a whole level from <paramref name="level"/> costs to train.</summary>
        public static double FullLevelCost(int level) => Math.Ceiling(Math.Round(TrainBaseCost * Math.Pow(TrainGrowth, level), 6));

        /// <summary>What `.train` costs for the rest of the next level: the XP already earned takes its share off. 0 at max level.</summary>
        public static double TrainCost(BattlePerson card)
        {
            if (card == null || card.Level >= MaxLevel) return 0;
            double done = Math.Clamp((double)card.Xp / XpToNext(card.Level), 0, 1);
            return Math.Max(1, Math.Ceiling(FullLevelCost(card.Level) * (1 - done)));
        }

        /// <summary>
        /// Trains as many levels as <paramref name="cash"/> pays for, up to <paramref name="maxLevels"/>. Returns the levels gained
        /// and what they cost; the caller takes the cost.
        /// </summary>
        public static (int Levels, double Cost) Train(BattlePerson card, double cash, int maxLevels)
        {
            int levels = 0;
            double cost = 0;
            while (levels < maxLevels && card.Level < MaxLevel)
            {
                double next = TrainCost(card);
                if (cost + next > cash) break;
                cost += next;
                card.Level++;
                card.Xp = 0;
                levels++;
            }
            return (levels, cost);
        }

        /// <summary>A card at the start of a new season: kept, but back to level 0 with no record.</summary>
        public static void ResetForNewSeason(BattlePerson card)
        {
            if (card == null) return;
            card.Level = 0;
            card.Xp = 0;
            card.Wins = 0;
            card.Losses = 0;
        }
    }
}
