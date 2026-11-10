using System.Collections.Generic;

namespace DiscordBot.Games.Models.BattleArena
{
    /// <summary>What one player's action did in a resolved round.</summary>
    public class RoundOutcome
    {
        public BattleArenaPlayer Player { get; set; }
        public BattleAction Action { get; set; }
        public int DiceRoll { get; set; }
        /// <summary>The opponent an Attack or Gamble was aimed at, or null.</summary>
        public BattleArenaPlayer Target { get; set; }
        public string Result { get; set; }
        public List<Hit> Hits { get; set; } = new List<Hit>();
    }

    public class Hit
    {
        public BattleArenaPlayer Target { get; set; }
        public double Damage { get; set; }
        public string Note { get; set; } //e.g. "Attack beats Focus", or null
    }
}
