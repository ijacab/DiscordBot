using DiscordBot.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Cards
{
    public enum PackKind
    {
        Street,
        Crew,
        Kingpin,
    }

    /// <summary>A card pack: how many cards, what it costs, and what it promises.</summary>
    public sealed class PackType
    {
        public PackKind Kind { get; init; }
        public string Name { get; init; }
        public string Emoji { get; init; }
        public int Cards { get; init; }
        public double Price { get; init; }
        /// <summary>One card in the pack is at least this rare.</summary>
        public CardRarity Guaranteed { get; init; }
        /// <summary>Legendary is this many times as likely in this pack.</summary>
        public double LegendaryBoost { get; init; } = 1;
    }

    /// <summary>
    /// A player's gang: many cards (<see cref="MaxCards"/>), opened in packs, fused up a rarity or scrapped for XP. A raid sends the
    /// best <see cref="CrewSize"/> cards that aren't lying low, and the best <see cref="CrewSize"/> always defend.
    /// </summary>
    public static class GangRules
    {
        public const int MaxCards = 30;
        public const int CrewSize = 3;
        /// <summary>A card that went on a raid lies low this long before it can go on another.</summary>
        public static readonly TimeSpan RestTime = TimeSpan.FromHours(1);
        /// <summary>A foil has this much more power.</summary>
        public const double FoilBonus = 0.15;
        /// <summary>How many cards of one rarity fuse into one of the next.</summary>
        public const int FuseCount = 3;
        /// <summary>Scrapping a card gives this much XP per level it had, on top of its rarity's (<see cref="ScrapXp"/>).</summary>
        public const int ScrapXpPerLevel = 25;

        public static readonly IReadOnlyList<PackType> Packs = new List<PackType>
        {
            new PackType { Kind = PackKind.Street, Name = "Street pack", Emoji = "📦", Cards = 3, Price = 3_000, Guaranteed = CardRarity.Common },
            new PackType { Kind = PackKind.Crew, Name = "Crew pack", Emoji = "🎁", Cards = 5, Price = 15_000, Guaranteed = CardRarity.Rare },
            new PackType { Kind = PackKind.Kingpin, Name = "Kingpin pack", Emoji = "💼", Cards = 5, Price = 150_000, Guaranteed = CardRarity.Epic, LegendaryBoost = 2 },
        };

        public static PackType Pack(PackKind kind) => Packs.First(p => p.Kind == kind);

        /// <summary>The pack a word names ("street", "crew", "kingpin", or the first letter), or null.</summary>
        public static PackType FindPack(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0) return null;
            return Packs.FirstOrDefault(p => p.Kind.ToString().ToLowerInvariant() == w || p.Kind.ToString().ToLowerInvariant().StartsWith(w));
        }

        /// <summary>The cards in a pack: each rolled normally, and the last one raised to the pack's guarantee if it fell short.</summary>
        public static List<BattlePerson> Open(PackType pack, Random random)
        {
            var cards = new List<BattlePerson>();
            for (int i = 0; i < pack.Cards; i++)
                cards.Add(BattlePerson.Pull(random, i == pack.Cards - 1 && cards.All(c => c.Rarity < pack.Guaranteed) ? pack.Guaranteed : CardRarity.Common, pack.LegendaryBoost));
            return cards;
        }

        public static string RarityEmoji(CardRarity rarity) =>
            rarity == CardRarity.Legendary ? "🟨" : rarity == CardRarity.Epic ? "🟪" : rarity == CardRarity.Rare ? "🟦" : "⬜";

        /// <summary>The roster strongest first; ties in a fixed order so the numbers on `.gang` don't jump about.</summary>
        public static List<BattlePerson> Ordered(IEnumerable<BattlePerson> cards) =>
            (cards ?? Enumerable.Empty<BattlePerson>()).OrderByDescending(c => c.Power).ThenByDescending(c => c.Level).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

        /// <summary>Who goes on a raid: the strongest cards that aren't lying low.</summary>
        public static List<BattlePerson> Crew(IEnumerable<BattlePerson> cards, DateTime now) =>
            Ordered(cards).Where(c => !c.IsResting(now)).Take(CrewSize).ToList();

        /// <summary>Who defends: the strongest cards, resting or not.</summary>
        public static List<BattlePerson> Defenders(IEnumerable<BattlePerson> cards) => Ordered(cards).Take(CrewSize).ToList();

        /// <summary>The card at a 1-based place in <see cref="Ordered"/>, as `.gang` numbers them.</summary>
        public static BattlePerson At(IList<BattlePerson> cards, int number)
        {
            var ordered = Ordered(cards);
            if (ordered.Count == 0) throw new BadInputException("You don't have a gang yet. `.pack` opens a street pack (the first each day is free).");
            if (number < 1 || number > ordered.Count) throw new BadInputException($"There's no card #{number}. You have {ordered.Count}; `.gang` numbers them.");
            return ordered[number - 1];
        }

        /// <summary>XP that scrapping this card gives another: its rarity's worth plus some for each level.</summary>
        public static int ScrapXp(BattlePerson card)
        {
            int base_ = card.Rarity == CardRarity.Legendary ? 800 : card.Rarity == CardRarity.Epic ? 300 : card.Rarity == CardRarity.Rare ? 120 : 50;
            return base_ + ScrapXpPerLevel * card.Level;
        }

        /// <summary>
        /// Fuses the <see cref="FuseCount"/> weakest cards of <paramref name="rarity"/> into one new card of the next rarity, at the highest
        /// level of the three and foil if any of them was. Removes them from <paramref name="cards"/> and adds the new one. Returns both.
        /// </summary>
        public static (List<BattlePerson> Used, BattlePerson Made) Fuse(List<BattlePerson> cards, CardRarity rarity, Random random)
        {
            if (rarity == CardRarity.Legendary) throw new BadInputException("Legendaries are as good as it gets: there's nothing to fuse them into.");
            var used = Ordered(cards.Where(c => c.Rarity == rarity)).AsEnumerable().Reverse().Take(FuseCount).ToList();
            if (used.Count < FuseCount)
                throw new BadInputException($"Fusing takes {FuseCount} {rarity} cards and you have {used.Count}.");
            var made = BattlePerson.Pull(random);
            made.Rarity = rarity + 1;
            made.Level = used.Max(c => c.Level);
            made.Foil = used.Any(c => c.Foil);
            foreach (var card in used) cards.Remove(card);
            cards.Add(made);
            return (used, made);
        }

        /// <summary>Reads "common", "rare", "epic" (or "c", "r", "e").</summary>
        public static bool TryParseRarity(string word, out CardRarity rarity)
        {
            string w = (word ?? "").Trim().ToLowerInvariant().TrimEnd('s');
            foreach (CardRarity r in Enum.GetValues(typeof(CardRarity)))
                if (w.Length > 0 && r.ToString().ToLowerInvariant().StartsWith(w)) { rarity = r; return true; }
            rarity = CardRarity.Common;
            return false;
        }

        private static readonly string[] WinCries =
        {
            "SMASH.", "Should've stayed in bed.", "I didn't even warm up.", "Pay up, buttercup.", "Who's next? Anyone? Fucking anyone?",
            "Not today, bitch.", "Easiest money I ever took.", "Check your pockets, genius.", "Thanks for the donation.", "Nice wallet. It's mine now.",
            "Finders keepers, fuckwit.", "Plot twist, dickhead.", "Smoke, mirrors, your money.", "Read 'em and weep.", "The house always wins. I'm the house.",
            "Pay the man, fuckface.", "In and out. Twenty minutes, tops.", "Nobody move, nobody gets hurt.",
        };

        private static readonly string[] LoseCries =
        {
            "Lucky punch. LUCKY.", "That was a warm-up round.", "Fuck. Again.", "Who put that wall there?", "Caught. Classic me.",
            "I'd like to speak to my lawyer.", "Nobody saw that. Right?", "Run. RUN.", "Rigged. Totally rigged.", "I had a plan. It was a bad plan.",
            "Should've cashed out.", "The dice are cursed.", "Somebody call a builder.", "Getaway driver went for a kebab.",
        };

        /// <summary>A line the winning (or losing) side shouts.</summary>
        public static string Cry(bool won, Random random)
        {
            var lines = won ? WinCries : LoseCries;
            return lines[random.Next(lines.Length)];
        }
    }
}
