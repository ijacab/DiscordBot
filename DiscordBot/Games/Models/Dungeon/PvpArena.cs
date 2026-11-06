using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Dungeon
{
    /// <summary>A surviving run entering PvP, with its stats and the haul it puts into the pot.</summary>
    public class PvpFighter
    {
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public int Hp { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        public double Haul { get; set; }
    }

    public class PvpResult
    {
        public List<string> Lines { get; } = new List<string>();
        public Dictionary<ulong, double> Payouts { get; } = new Dictionary<ulong, double>();
        public double Pot { get; set; }
    }

    /// <summary>
    /// Auto-resolved, free-for-all PvP. The pot is the sum of the entrants' hauls, and the winner takes all.
    /// Each round every living fighter attacks a random other living fighter. The fight stops when one is left,
    /// or at <see cref="MaxRounds"/>, when the highest HP wins and ties split the pot.
    /// Pure game logic, so it can be tested with a seeded Random.
    /// </summary>
    public static class PvpArena
    {
        /// <summary>Round cap. A proposal, to confirm with the user.</summary>
        public const int MaxRounds = 10;

        private sealed class Combatant
        {
            public PvpFighter Fighter;
            public int Hp;
        }

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
                return result;
            }

            var fighters = entrants.Select(e => new Combatant { Fighter = e, Hp = e.Hp }).ToList();
            var alive = fighters.ToList();
            result.Lines.Add($"PvP begins with {alive.Count} fighters. The pot is ${result.Pot:N2}.");

            int round = 0;
            while (alive.Count > 1 && round < MaxRounds)
            {
                round++;
                foreach (Combatant attacker in alive.ToList())
                {
                    if (alive.Count <= 1)
                        break;
                    if (attacker.Hp <= 0)
                        continue;

                    var targets = alive.Where(c => c != attacker).ToList();
                    Combatant target = targets[random.Next(targets.Count)];
                    int damage = Math.Max(1, attacker.Fighter.Attack + random.Next(1, 7) - target.Fighter.Defense);
                    target.Hp -= damage;
                    result.Lines.Add($"Round {round}: {attacker.Fighter.Name} hits {target.Fighter.Name} for {damage}.");

                    if (target.Hp <= 0)
                    {
                        alive.Remove(target);
                        result.Lines.Add($"{target.Fighter.Name} falls.");
                    }
                }
            }

            List<Combatant> winners;
            if (alive.Count == 1)
            {
                winners = alive;
                result.Lines.Add($"{alive[0].Fighter.Name} is the last one standing.");
            }
            else
            {
                int highest = alive.Max(c => c.Hp);
                winners = alive.Where(c => c.Hp == highest).ToList();
                result.Lines.Add($"The round limit of {MaxRounds} is reached. The highest HP wins{(winners.Count > 1 ? ", so the pot is shared" : "")}.");
            }

            IReadOnlyDictionary<ulong, double> split = DungeonRun.SplitHaul(result.Pot, winners.Select(w => w.Fighter.UserId).ToList());
            foreach (KeyValuePair<ulong, double> payout in split)
                result.Payouts[payout.Key] = payout.Value;
            return result;
        }
    }
}
