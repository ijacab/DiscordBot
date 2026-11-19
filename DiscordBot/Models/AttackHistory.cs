using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>One hit on a player, kept on their account for `.attacks`: who, what with, what it cost them and how it went.</summary>
    public class AttackRecord
    {
        public long WhenUtcTicks { get; set; }
        public ulong AttackerId { get; set; }
        public string AttackerName { get; set; }
        /// <summary>What was used: a power-up name, "Raid", "Revenge raid" or "Daily roll steal".</summary>
        public string Source { get; set; }
        /// <summary>Money the victim lost to it. Negative when they came out ahead (a raid that failed). 0 for a freeze, a bounced hit and so on.</summary>
        public double Lost { get; set; }
        /// <summary>A short note on how it went, like "$300 of it on your debt" or "bounced off your Mirror".</summary>
        public string Detail { get; set; }
    }

    /// <summary>The newest-first list of attacks on a player (<see cref="CoinAccounts.CoinAccount.RecentAttacks"/>) and how it reads.</summary>
    public static class AttackHistory
    {
        /// <summary>How many attacks are kept per player.</summary>
        public const int MaxKept = 25;
        /// <summary>How many `.attacks` shows when you don't say.</summary>
        public const int DefaultShown = 5;

        /// <summary>Notes an attack on <paramref name="victim"/>. Saved with the next account save, like every other change.</summary>
        public static void Record(CoinAccounts.CoinAccount victim, ulong attackerId, string attackerName, string source, double lost, string detail, DateTime now)
        {
            if (victim == null) return;
            victim.RecentAttacks.Insert(0, new AttackRecord
            {
                WhenUtcTicks = now.Ticks,
                AttackerId = attackerId,
                AttackerName = attackerName,
                Source = source,
                Lost = Math.Round(lost, 2),
                Detail = detail,
            });
            if (victim.RecentAttacks.Count > MaxKept)
                victim.RecentAttacks.RemoveRange(MaxKept, victim.RecentAttacks.Count - MaxKept);
        }

        public static string Ago(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m ago";
            return $"{(int)span.TotalDays}d {span.Hours}h ago";
        }

        private static string Dollars(double amount) => "$" + Common.Helpers.FormatHelper.GetCommaNumber(Math.Abs(amount));

        /// <summary>One line: when, who, what they used, and what it cost (or earned) you.</summary>
        public static string Describe(AttackRecord r, DateTime now)
        {
            string icon = r.Lost > 0 ? "🔴" : r.Lost < 0 ? "🟢" : "⚪";
            string money = r.Lost > 0 ? $"you lost **{Dollars(r.Lost)}**" : r.Lost < 0 ? $"you won **{Dollars(r.Lost)}**" : "no money moved";
            string detail = string.IsNullOrWhiteSpace(r.Detail) ? "" : $" ({r.Detail})";
            return $"{icon} {Ago(now - new DateTime(r.WhenUtcTicks, DateTimeKind.Utc))} · **{r.AttackerName ?? "someone"}** · {r.Source}: {money}{detail}";
        }

        /// <summary>Last 24 hours: how many attacks and what they cost you in all (gains from failed raids count against it).</summary>
        public static (int Count, double Net) Last24Hours(CoinAccounts.CoinAccount victim, DateTime now)
        {
            var recent = victim.RecentAttacks.Where(a => now.Ticks - a.WhenUtcTicks < TimeSpan.TicksPerDay).ToList();
            return (recent.Count, recent.Sum(a => a.Lost));
        }
    }
}
