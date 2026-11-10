using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.BattleArena
{
    /// <summary>
    /// Side bets from people watching a Face Off. Each backer puts money on one fighter, until betting closes when round 1
    /// resolves. When the fight ends, everything backed is shared among the people who backed a winner, in proportion to
    /// their stakes. If nobody backed a winner, everyone gets their stake back. No fee is taken.
    /// Pure bookkeeping: the manager takes the stakes and pays the payouts.
    /// </summary>
    public class SpectatorPool
    {
        public sealed class Backing
        {
            public ulong BackerId { get; set; }
            public string BackerName { get; set; }
            public ulong FighterId { get; set; }
            public double Amount { get; set; }
        }

        private readonly Dictionary<ulong, Backing> _backings = new Dictionary<ulong, Backing>();

        public bool IsOpen { get; private set; } = true;
        public IReadOnlyCollection<Backing> Backings => _backings.Values;
        public double Total => _backings.Values.Sum(b => b.Amount);
        public double TotalOn(ulong fighterId) => _backings.Values.Where(b => b.FighterId == fighterId).Sum(b => b.Amount);
        public Backing BackingOf(ulong backerId) => _backings.TryGetValue(backerId, out Backing backing) ? backing : null;

        /// <summary>What a backer of this fighter would get per $1 if the fighter won alone. Null when nobody backs them yet.</summary>
        public double? PayoutMultiple(ulong fighterId)
        {
            double on = TotalOn(fighterId);
            return on > 0 ? Total / on : (double?)null;
        }

        /// <summary>Adds a stake on a fighter. Returns a refusal, or null when it worked. The caller takes the money on success.</summary>
        public string Back(ulong backerId, string backerName, ulong fighterId, double amount, IReadOnlyCollection<ulong> fighterIds)
        {
            if (!IsOpen) return "Betting on this fight has closed.";
            if (fighterIds.Contains(backerId)) return "You're fighting in this one, so you can't back anyone.";
            if (!fighterIds.Contains(fighterId)) return "That fighter isn't in this fight.";
            if (amount < 1) return "Back with at least $1.";

            if (_backings.TryGetValue(backerId, out Backing existing))
            {
                if (existing.FighterId != fighterId)
                    return "You've already backed someone else in this fight. You can only add to that backing.";
                existing.Amount = Math.Round(existing.Amount + amount, 2);
                return null;
            }

            _backings[backerId] = new Backing { BackerId = backerId, BackerName = backerName, FighterId = fighterId, Amount = Math.Round(amount, 2) };
            return null;
        }

        /// <summary>Closes betting. Called when round 1 resolves.</summary>
        public void Close() => IsOpen = false;

        /// <summary>Refunds the backings on a fighter who left the lobby, and returns them.</summary>
        public List<Backing> RemoveFighter(ulong fighterId)
        {
            var refunds = _backings.Values.Where(b => b.FighterId == fighterId).ToList();
            foreach (Backing backing in refunds)
                _backings.Remove(backing.BackerId);
            return refunds;
        }

        /// <summary>
        /// What each backer is paid when the fight ends. The whole pool goes to backers of the winners by stake, rounded down
        /// to cents with the leftover cents to the biggest stake. With no backer on any winner, every stake comes back.
        /// </summary>
        public Dictionary<ulong, double> Settle(IReadOnlyCollection<ulong> winnerIds)
        {
            IsOpen = false;
            var payouts = new Dictionary<ulong, double>();
            var winning = _backings.Values.Where(b => winnerIds.Contains(b.FighterId)).OrderByDescending(b => b.Amount).ToList();
            if (winning.Count == 0)
            {
                foreach (Backing backing in _backings.Values)
                    payouts[backing.BackerId] = backing.Amount;
                return payouts;
            }

            double pool = Total;
            double winningStake = winning.Sum(b => b.Amount);
            foreach (Backing backing in winning)
                payouts[backing.BackerId] = Math.Floor(pool * backing.Amount / winningStake * 100) / 100;
            double leftover = Math.Round(pool - payouts.Values.Sum(), 2);
            payouts[winning[0].BackerId] = Math.Round(payouts[winning[0].BackerId] + leftover, 2);
            return payouts;
        }

        /// <summary>Every stake back, for a fight that never happened.</summary>
        public Dictionary<ulong, double> RefundAll()
        {
            IsOpen = false;
            return _backings.Values.ToDictionary(b => b.BackerId, b => b.Amount);
        }
    }
}
