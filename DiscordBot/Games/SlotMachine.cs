using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public enum SlotSymbol { Cherry, Lemon, Orange, Grape, Bell, Star, Seven, Diamond }

    /// <summary>What one spin showed: three reels, each with the symbol above, on and below the payline.</summary>
    public sealed class SlotResult
    {
        public const int Reels = 3;
        public const int Rows = 3;
        public const int PaylineRow = 1;

        /// <summary>Where each reel stopped on its strip.</summary>
        public int[] Stops { get; init; }
        /// <summary>The window: [reel][row], with row 1 being the payline.</summary>
        public SlotSymbol[][] Window { get; init; }
        public SlotSymbol[] Line => Window.Select(reel => reel[PaylineRow]).ToArray();
        /// <summary>What comes back as a multiple of the bet, counting the stake. 0 is a loss. For a jackpot it's the floor; the pool decides the rest.</summary>
        public double Multiple { get; init; }
        public bool Jackpot { get; init; }
        public string Label { get; init; }
    }

    /// <summary>
    /// A three-reel slot machine, as pure rules with the Random injected. Every reel is the same weighted strip of
    /// <see cref="StripLength"/> stops, rotated so the reels don't line up. The line pays by the table below, and three
    /// diamonds win the jackpot pool (see <see cref="JackpotFloorMultiple"/> and <see cref="JackpotCapMultiple"/>).
    /// A test works out the exact return by going through every combination of stops.
    /// </summary>
    public static class SlotMachine
    {
        public const int StripLength = 30;
        /// <summary>Three diamonds always pay at least this many times the bet.</summary>
        public const double JackpotFloorMultiple = 100;
        /// <summary>...and take at most this many times the bet from the pool, so a $1 spin can't take millions.</summary>
        public const double JackpotCapMultiple = 2000;

        /// <summary>How many of the strip's stops each symbol takes.</summary>
        private static readonly Dictionary<SlotSymbol, int> Counts = new Dictionary<SlotSymbol, int>
        {
            [SlotSymbol.Cherry] = 7, [SlotSymbol.Lemon] = 6, [SlotSymbol.Orange] = 5, [SlotSymbol.Grape] = 4,
            [SlotSymbol.Bell] = 3, [SlotSymbol.Star] = 2, [SlotSymbol.Seven] = 2, [SlotSymbol.Diamond] = 1,
        };

        /// <summary>Three of a kind pays this many times the bet (diamonds are the jackpot).</summary>
        private static readonly Dictionary<SlotSymbol, double> ThreeOfAKind = new Dictionary<SlotSymbol, double>
        {
            [SlotSymbol.Cherry] = 8, [SlotSymbol.Lemon] = 12, [SlotSymbol.Orange] = 20, [SlotSymbol.Grape] = 30,
            [SlotSymbol.Bell] = 60, [SlotSymbol.Star] = 120, [SlotSymbol.Seven] = 250, [SlotSymbol.Diamond] = JackpotFloorMultiple,
        };

        public const double TwoCherriesMultiple = 2;
        public const double LeftCherryMultiple = 1;

        public static double PayFor(SlotSymbol symbol) => ThreeOfAKind[symbol];

        public static string Emoji(SlotSymbol symbol)
        {
            switch (symbol)
            {
                case SlotSymbol.Cherry: return "🍒";
                case SlotSymbol.Lemon: return "🍋";
                case SlotSymbol.Orange: return "🍊";
                case SlotSymbol.Grape: return "🍇";
                case SlotSymbol.Bell: return "🔔";
                case SlotSymbol.Star: return "⭐";
                case SlotSymbol.Seven: return "7️⃣";
                default: return "💎";
            }
        }

        private static readonly SlotSymbol[][] Strips = Enumerable.Range(0, SlotResult.Reels).Select(BuildStrip).ToArray();

        /// <summary>Spreads each symbol evenly round the strip, then turns it a different amount for each reel.</summary>
        private static SlotSymbol[] BuildStrip(int reel)
        {
            var spread = Counts
                .SelectMany(c => Enumerable.Range(0, c.Value).Select(k => (Position: (k + 0.5) / c.Value, Symbol: c.Key)))
                .OrderBy(p => p.Position).ThenBy(p => p.Symbol)
                .Select(p => p.Symbol)
                .ToArray();
            int turn = reel * 11;
            return Enumerable.Range(0, StripLength).Select(i => spread[(i + turn) % StripLength]).ToArray();
        }

        public static SlotResult Spin(Random random)
        {
            var stops = new int[SlotResult.Reels];
            for (int i = 0; i < stops.Length; i++)
                stops[i] = random.Next(StripLength);
            return At(stops);
        }

        /// <summary>The result for particular stops. Spin uses this, and so do the tests.</summary>
        public static SlotResult At(int[] stops)
        {
            var window = new SlotSymbol[SlotResult.Reels][];
            for (int reel = 0; reel < window.Length; reel++)
                window[reel] = Enumerable.Range(0, SlotResult.Rows).Select(row => SymbolAt(reel, stops[reel] + row - SlotResult.PaylineRow)).ToArray();

            var (multiple, jackpot, label) = Evaluate(window.Select(r => r[SlotResult.PaylineRow]).ToArray());
            return new SlotResult { Stops = stops, Window = window, Multiple = multiple, Jackpot = jackpot, Label = label };
        }

        /// <summary>A symbol anywhere on a reel's strip, wrapping round. Used for the symbols above and below the line, and for spinning frames.</summary>
        public static SlotSymbol SymbolAt(int reel, int stop)
        {
            int length = Strips[reel].Length;
            return Strips[reel][((stop % length) + length) % length];
        }

        public static (double Multiple, bool Jackpot, string Label) Evaluate(SlotSymbol[] line)
        {
            if (line[0] == line[1] && line[1] == line[2])
            {
                SlotSymbol symbol = line[0];
                if (symbol == SlotSymbol.Diamond)
                    return (JackpotFloorMultiple, true, "💎💎💎 JACKPOT");
                return (ThreeOfAKind[symbol], false, $"Three {Emoji(symbol)}");
            }

            int cherries = line.Count(s => s == SlotSymbol.Cherry);
            if (cherries == 2)
                return (TwoCherriesMultiple, false, "Two 🍒");
            if (cherries == 1 && line[0] == SlotSymbol.Cherry)
                return (LeftCherryMultiple, false, "🍒 on the left");
            return (0, false, "No luck");
        }

        /// <summary>
        /// What the bet gets back on average without the jackpot pool (a jackpot counts at its floor), worked out by going through
        /// every combination of stops. Below 1 means the house wins over time.
        /// </summary>
        public static double ReturnToPlayer()
        {
            double total = 0;
            for (int a = 0; a < StripLength; a++)
                for (int b = 0; b < StripLength; b++)
                    for (int c = 0; c < StripLength; c++)
                        total += At(new[] { a, b, c }).Multiple;
            return total / ((double)StripLength * StripLength * StripLength);
        }

        /// <summary>The extra return if every jackpot paid its cap instead of its floor.</summary>
        public static double ExtraReturnAtJackpotCap()
        {
            double diamondOnly = Counts[SlotSymbol.Diamond] / (double)StripLength;
            return diamondOnly * diamondOnly * diamondOnly * (JackpotCapMultiple - JackpotFloorMultiple);
        }

        /// <summary>What three diamonds pay: the pool, but at least the floor and at most the cap. Only the part from the pool is taken from it.</summary>
        public static double JackpotPayout(double bet, double pool) =>
            Math.Clamp(pool, bet * JackpotFloorMultiple, bet * JackpotCapMultiple);
    }
}
