using System;

namespace DiscordBot.Games
{
    /// <summary>
    /// Crash (`.crash`): a multiplier climbs from 1.00x and the rocket blows up at a point fixed when it launches. Cash out
    /// before it does and you get your bet times the multiplier; wait too long and it's gone. The crash point is drawn so that
    /// P(it reaches m) = (1 - <see cref="HouseEdge"/>) / m, so whatever you aim for, you get back 97% on average: no strategy
    /// beats it and none is worse. <see cref="HouseEdge"/> of launches blow up on the pad.
    /// </summary>
    public static class CrashGame
    {
        public const double HouseEdge = 0.03;
        /// <summary>The rocket never goes past this; reaching it cashes you out automatically.</summary>
        public const double MaxMultiplier = 100;
        /// <summary>The multiplier is e^(rate x seconds): 2x after about 6 s, 10x after 19 s, 100x after 38 s.</summary>
        public const double GrowthPerSecond = 0.12;
        public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1.3);

        /// <summary>Where a launch blows up, from a uniform random number in [0, 1).</summary>
        public static double CrashPoint(double uniform)
        {
            uniform = Math.Clamp(uniform, 0, 0.9999999);
            double point = (1 - HouseEdge) / (1 - uniform);
            if (point < 1) return 1; //blew up on the pad
            return Math.Min(MaxMultiplier, Math.Max(1.01, Math.Floor(point * 100) / 100)); //anything that gets off the pad reaches 1.01x
        }

        /// <summary>The multiplier after the rocket has been up this long.</summary>
        public static double MultiplierAt(TimeSpan elapsed) =>
            Math.Min(MaxMultiplier, Math.Floor(Math.Exp(GrowthPerSecond * Math.Max(0, elapsed.TotalSeconds)) * 100) / 100);

        /// <summary>A target like "2x", "1.5x" or "2": an automatic cash-out. Must be above 1x and at most the cap.</summary>
        public static bool TryParseTarget(string text, out double target)
        {
            target = 0;
            string t = (text ?? "").Trim().ToLowerInvariant().TrimEnd('x');
            return double.TryParse(t, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out target)
                && double.IsFinite(target) && target >= 1.01 && target <= MaxMultiplier;
        }

        /// <summary>The chance a launch gets to <paramref name="multiplier"/>.</summary>
        public static double ChanceToReach(double multiplier) => multiplier <= 1 ? 1 : Math.Min(1, (1 - HouseEdge) / multiplier);
    }
}
