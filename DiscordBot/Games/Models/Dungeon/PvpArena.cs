using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Dungeon
{
    /// <summary>A surviving run entering PvP. <see cref="Stats"/> should be a copy, so the arena doesn't change the run.</summary>
    public class PvpFighter
    {
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public Combatant Stats { get; set; }
        public double Haul { get; set; }
    }

    public class PvpResult
    {
        public List<string> Lines { get; } = new List<string>();
        public Dictionary<ulong, double> Payouts { get; } = new Dictionary<ulong, double>();
        public double Pot { get; set; }
        public List<string> Winners { get; } = new List<string>();
    }

    /// <summary>
    /// Auto-resolved, free-for-all PvP. The pot is the sum of the entrants' hauls, and the winner takes all.
    /// Each round every living fighter attacks a random other living fighter, using the shared combat rules.
    /// The fight stops when one is left, or at <see cref="Combat.MaxRounds"/> (10), when the highest HP wins and ties split the pot.
    /// Pure game logic, so it can be tested with a seeded Random.
    /// </summary>
    public static class PvpArena
    {
        public const int MaxRounds = Combat.MaxRounds;

        /// <summary>Extra attack for every PvP fighter, so fights end sooner. Tuned with the sim.</summary>
        public const int AttackBonus = 3;

        public static PvpResult Resolve(IReadOnlyList<PvpFighter> entrants, Random random)
        {
            var result = new PvpResult();
            result.Pot = Math.Round(entrants.Sum(e => e.Haul), 2);

            if (entrants.Count == 0)
            {
                result.Lines.Add("No one is left standing, so nothing is paid out.");
                return result;
            }
            if (entrants.Count == 1)
            {
                result.Lines.Add($"{entrants[0].Name} is the only one left, and keeps their haul.");
                result.Payouts[entrants[0].UserId] = result.Pot;
                result.Winners.Add(entrants[0].Name);
                return result;
            }

            foreach (PvpFighter entrant in entrants)
                entrant.Stats.Attack += AttackBonus;

            var alive = entrants.ToList();
            result.Lines.Add($"PvP begins with {alive.Count} fighters. The pot is ${result.Pot:N2}.");

            int round = 0;
            while (alive.Count > 1 && round < MaxRounds)
            {
                round++;
                foreach (PvpFighter fighter in alive.ToList())
                    Combat.StartRound(fighter.Stats);

                foreach (PvpFighter attacker in alive.ToList())
                {
                    if (alive.Count <= 1)
                        break;
                    if (!attacker.Stats.Alive)
                        continue;

                    PvpFighter target = alive.Where(f => f != attacker).ToList()[random.Next(alive.Count - 1)];
                    AttackOutcome outcome = Combat.Attack(attacker.Stats, target.Stats, random);
                    result.Lines.Add(Describe(round, attacker.Name, target.Name, outcome));

                    if (Combat.TrySecondWind(target.Stats))
                        result.Lines.Add($"{target.Name} finds Second Wind.");

                    if (!target.Stats.Alive)
                    {
                        alive.Remove(target);
                        result.Lines.Add($"{target.Name} falls.");
                    }
                    if (!attacker.Stats.Alive)
                    {
                        alive.Remove(attacker);
                        result.Lines.Add($"{attacker.Name} falls to their own thorns.");
                    }
                }
            }

            List<PvpFighter> winners;
            if (alive.Count == 1)
            {
                winners = alive;
                result.Lines.Add($"{alive[0].Name} is the last one standing.");
            }
            else
            {
                int highest = alive.Max(f => f.Stats.Hp);
                winners = alive.Where(f => f.Stats.Hp == highest).ToList();
                result.Lines.Add($"The round limit of {MaxRounds} is reached. The highest HP wins{(winners.Count > 1 ? ", so the pot is shared" : "")}.");
            }

            IReadOnlyDictionary<ulong, double> split = DungeonRun.SplitHaul(result.Pot, winners.Select(w => w.UserId).ToList());
            foreach (KeyValuePair<ulong, double> payout in split)
                result.Payouts[payout.Key] = payout.Value;
            result.Winners.AddRange(winners.Select(w => w.Name));
            return result;
        }

        private static string Describe(int round, string attacker, string target, AttackOutcome outcome)
        {
            if (!outcome.Hit)
                return $"Round {round}: {attacker} swings at {target} and misses.";
            string crit = outcome.Crit ? " CRITICAL" : "";
            string lifesteal = outcome.Healed > 0 ? $" ({attacker} heals {outcome.Healed})" : "";
            string thorns = outcome.Reflected > 0 ? $" ({target}'s thorns hit {attacker} for {outcome.Reflected})" : "";
            return $"Round {round}: {attacker} hits {target} for {outcome.Damage}.{crit}{lifesteal}{thorns}";
        }
    }
}
