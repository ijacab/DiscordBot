using System;

namespace DiscordBot.Games.Models.BattleArena
{
    /// <summary>What a player does in a round. Each player picks one action per round, and every action rolls one d20.</summary>
    public enum BattleAction
    {
        Attack,
        Defend,
        Focus,
        Gamble
    }

    public static class BattleActionInfo
    {
        /// <summary>Focus adds this to the next Attack or Gamble roll.</summary>
        public const int FocusBonus = 5;

        /// <summary>Shown to players so they know what beats what.</summary>
        public const string CounterRules = "Attack beats Focus, Focus beats Gamble, Gamble beats Defend, Defend beats Attack.";

        public static bool TryParse(string text, out BattleAction action)
        {
            switch (text?.ToLowerInvariant())
            {
                case "attack": action = BattleAction.Attack; return true;
                case "defend": action = BattleAction.Defend; return true;
                case "focus": action = BattleAction.Focus; return true;
                case "gamble": action = BattleAction.Gamble; return true;
                default: action = default; return false;
            }
        }

        public static string Describe(this BattleAction action)
        {
            switch (action)
            {
                case BattleAction.Attack: return "Hits every opponent. A roll of 20 is a critical hit, 11 to 19 hits harder, and a roll of 1 hurts you.";
                case BattleAction.Defend: return "Takes half the damage you would receive this round. A roll of 20 blocks everything, and a roll of 1 fails. Counters Attack.";
                case BattleAction.Focus: return "Gives your next Attack or Gamble roll +5. You do nothing else this round. Counters Gamble.";
                case BattleAction.Gamble: return "Big swings: a roll of 15 to 19 hits everyone hard, 20 is a jackpot crit on everyone, and 1 to 5 backfires on you.";
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
    }
}
