using System;
using System.Collections.Generic;

namespace DiscordBot.Games.Models.Cards
{
    /// <summary>
    /// What a Battle Person is good at. Each pull is one of five (the numbers are the old ability slots, so saved cards keep their kind;
    /// <see cref="Medic"/> is the old Heal, now a Guardian, and <see cref="None"/> has no trait).
    /// </summary>
    public enum Archetype
    {
        None = 0,
        Medic = 1,
        Guardian = 2,
        Trickster = 3,
        Brute = 4,
        Thief = 5,
        Gambler = 6,
    }

    /// <summary>
    /// The five archetypes and what they do in a raid. Brute beats Thief, Thief beats Guardian, Guardian beats Brute (+20% power
    /// against the one it counters); Trickster and Gambler have no counter and are countered by nobody. Every trait has a price, so none
    /// is simply better: the raid rules in <see cref="Models.RaidRules"/> and the numbers here are tested to stay within a fair band.
    /// </summary>
    public static class Archetypes
    {
        public static readonly Archetype[] Pullable = { Archetype.Brute, Archetype.Guardian, Archetype.Thief, Archetype.Trickster, Archetype.Gambler };

        /// <summary>A Brute hits harder and takes more.</summary>
        public const double BruteAttack = 1.15, BruteDefence = 0.90;
        /// <summary>A Guardian is hard to crack, and stays safe for longer after a raid gets through, but is no great raider.</summary>
        public const double GuardianDefence = 1.15;
        public static readonly TimeSpan GuardianProtection = TimeSpan.FromHours(2);
        /// <summary>Power against the archetype this one counters (and against it, if you are the one countered).</summary>
        public const double CounterBonus = 0.20;
        /// <summary>A Thief's win pays this many times the stake, but a failed raid costs this much extra.</summary>
        public const double ThiefLootMultiple = 2.5, ThiefFailedRaidExtra = 0.25;
        /// <summary>A Gambler's power swings this much up or down on a coin flip every raid; a natural 100 doubles the loot and a natural 1 doubles the loss.</summary>
        public const double GamblerSwing = 0.25;

        public static Archetype Normalise(Archetype a) => a == Archetype.Medic ? Archetype.Guardian : a;

        public static string Name(Archetype a)
        {
            switch (Normalise(a))
            {
                case Archetype.Brute: return "Brute";
                case Archetype.Guardian: return "Guardian";
                case Archetype.Thief: return "Thief";
                case Archetype.Trickster: return "Trickster";
                case Archetype.Gambler: return "Gambler";
                default: return "Rookie";
            }
        }

        public static string Emoji(Archetype a)
        {
            switch (Normalise(a))
            {
                case Archetype.Brute: return "🥊";
                case Archetype.Guardian: return "🛡️";
                case Archetype.Thief: return "🦝";
                case Archetype.Trickster: return "🃏";
                case Archetype.Gambler: return "🎰";
                default: return "🙂";
            }
        }

        public static string Label(Archetype a) => $"{Emoji(a)} {Name(a)}";

        /// <summary>Whether <paramref name="a"/> counters <paramref name="b"/>: Brute beats Thief beats Guardian beats Brute.</summary>
        public static bool Counters(Archetype a, Archetype b)
        {
            a = Normalise(a);
            b = Normalise(b);
            return (a == Archetype.Brute && b == Archetype.Thief)
                || (a == Archetype.Thief && b == Archetype.Guardian)
                || (a == Archetype.Guardian && b == Archetype.Brute);
        }

        public static Archetype? Beats(Archetype a) =>
            Normalise(a) == Archetype.Brute ? Archetype.Thief : Normalise(a) == Archetype.Thief ? Archetype.Guardian : Normalise(a) == Archetype.Guardian ? Archetype.Brute : (Archetype?)null;

        public static Archetype? WeakTo(Archetype a) =>
            Normalise(a) == Archetype.Thief ? Archetype.Brute : Normalise(a) == Archetype.Guardian ? Archetype.Thief : Normalise(a) == Archetype.Brute ? Archetype.Guardian : (Archetype?)null;

        /// <summary>Multiplies the power of a card raiding.</summary>
        public static double AttackMultiplier(Archetype a) => Normalise(a) == Archetype.Brute ? BruteAttack : 1;

        /// <summary>Multiplies the power of a card defending.</summary>
        public static double DefenceMultiplier(Archetype a) =>
            Normalise(a) == Archetype.Brute ? BruteDefence : Normalise(a) == Archetype.Guardian ? GuardianDefence : 1;

        /// <summary>How many times the stake a winning raid takes from the target.</summary>
        public static double LootMultiple(Archetype a, double normal) => Normalise(a) == Archetype.Thief ? ThiefLootMultiple : normal;

        private static string Pct(double multiple) => $"{Math.Abs(multiple - 1) * 100:0}%";

        /// <summary>One or two lines on what the archetype does, from the real numbers.</summary>
        public static string Trait(Archetype a)
        {
            string counters = Beats(a) is Archetype beats && WeakTo(a) is Archetype weak
                ? $" Beats {Label(beats)}, loses to {Label(weak)} (+{CounterBonus * 100:0}% power)."
                : " No counter, and nobody counters it.";
            switch (Normalise(a))
            {
                case Archetype.Brute: return $"+{Pct(BruteAttack)} power raiding, -{Pct(BruteDefence)} defending.{counters}";
                case Archetype.Guardian: return $"+{Pct(GuardianDefence)} power defending, and {GuardianProtection.TotalHours:0} hours of protection after a raid gets through (not 1).{counters}";
                case Archetype.Thief: return $"A win takes {ThiefLootMultiple:0.#}x your stake instead of 2x, but a failed raid costs {ThiefFailedRaidExtra * 100:0}% extra.{counters}";
                case Archetype.Trickster: return $"Once a day a failed raid is rolled again.{counters}";
                case Archetype.Gambler: return $"Power swings {GamblerSwing * 100:0}% up or down on a coin flip every raid. A natural 100 doubles the loot, a natural 1 doubles the loss.{counters}";
                default: return "No trait.";
            }
        }

        // ---- what they shout

        private static readonly Dictionary<Archetype, string[]> _winCries = new Dictionary<Archetype, string[]>
        {
            [Archetype.Brute] = new[] { "SMASH.", "Should've stayed in bed.", "I didn't even warm up.", "Pay up, buttercup.", "Who's next? Anyone? Fucking anyone?", "That's what happens, tiny man." },
            [Archetype.Guardian] = new[] { "Not today, bitch.", "The wall holds.", "Come back with a bigger fist.", "Denied. Stamped. Filed.", "Is that all you've got?", "Knock knock. Nobody's home, dickhead." },
            [Archetype.Thief] = new[] { "Easiest money I ever took.", "Check your pockets, genius.", "Thanks for the donation.", "You never even felt it.", "Nice wallet. It's mine now.", "Finders keepers, fuckwit." },
            [Archetype.Trickster] = new[] { "Plot twist, dickhead.", "It was all part of the plan. (It wasn't.)", "Smoke, mirrors, your money.", "You fell for the oldest trick.", "Ta-da. Where's your cash gone?", "Never trust a smile." },
            [Archetype.Gambler] = new[] { "Read 'em and weep.", "Told you I'd hit.", "The house always wins. I'm the house.", "Double or nothing? Too late, I've got it all.", "Lady Luck loves me.", "Pay the man, fuckface." },
            [Archetype.None] = new[] { "Easy.", "Hah!", "Better luck next time." },
        };

        private static readonly Dictionary<Archetype, string[]> _loseCries = new Dictionary<Archetype, string[]>
        {
            [Archetype.Brute] = new[] { "My knuckles are fine. I'm fine.", "Lucky punch. LUCKY.", "That was a warm-up round.", "I let you win. Obviously.", "Fuck. Again.", "Who put that wall there?" },
            [Archetype.Guardian] = new[] { "The wall has... cracks.", "Somebody call a builder.", "That's coming out of my pension.", "I was looking the other way.", "Okay that one hurt.", "I'll be in my bunker." },
            [Archetype.Thief] = new[] { "Caught. Classic me.", "I'd like to speak to my lawyer.", "That wallet was a trap.", "Nobody saw that. Right?", "Fucking amateur hour.", "Run. RUN." },
            [Archetype.Trickster] = new[] { "The trick was on me.", "Fine. That one's on me.", "Rigged. Totally rigged.", "I had a plan. It was a bad plan.", "Where did my sleeve go?", "Shit. The mirror was cracked." },
            [Archetype.Gambler] = new[] { "Snake eyes. Story of my life.", "Should've cashed out.", "One more and I'll win it back.", "The dice are cursed.", "Double down. Obviously.", "I'm not crying, you're crying." },
            [Archetype.None] = new[] { "Ouch.", "Not my day.", "Shit." },
        };

        /// <summary>A line the winner (or the loser) of a raid shouts, in the archetype's voice.</summary>
        public static string Cry(Archetype a, bool won, Random random)
        {
            var table = won ? _winCries : _loseCries;
            var lines = table.TryGetValue(Normalise(a), out var found) ? found : table[Archetype.None];
            return lines[random.Next(lines.Length)];
        }
    }
}
