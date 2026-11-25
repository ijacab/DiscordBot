namespace DiscordBot.Models
{
    /// <summary>
    /// Component id prefixes (see <see cref="ComponentId"/>) for the money buttons, and who may press them.
    /// Buttons on shared things (the market, a trade result, a game lobby) act for whoever clicks. Buttons on one person's
    /// own view (their portfolio, power-ups, auto-sell) carry that person's id and only work for them.
    /// </summary>
    public static class MoneyComponents
    {
        public const string Stock = "stk";
        public const string PowerUp = "pu";
        public const string Property = "prop";
        public const string Debt = "debt";
        /// <summary>The slot machine (Games/SlotView.cs): spin, bet and cash-out buttons for the player whose machine it is.</summary>
        public const string Slots = "slot";
        /// <summary>The roulette result buttons (Games/RouletteView.cs): spin again, scale the bets or place new ones. Anyone can press them; each press plays for whoever pressed it.</summary>
        public const string Roulette = "rlt";
        /// <summary>The page picker under `.stats`.</summary>
        public const string Stats = "stats";
        /// <summary>The control panel under `.card`: train, guards, stance and focus, for the card's owner.</summary>
        public const string Card = "card";
        /// <summary>Raid buttons: stake buttons under a `.raid @person` preview (for the raider) and Revenge under a result (for the raided).</summary>
        public const string Raid = "raid";
    }

    public static class ButtonOwner
    {
        public static bool Allows(ulong ownerId, ulong clickerId) => ownerId == clickerId;

        /// <summary>Reads the owner id a button carries. False if it's missing or malformed.</summary>
        public static bool TryRead(string[] args, int position, out ulong ownerId)
        {
            ownerId = 0;
            return args.Length > position && ulong.TryParse(args[position], out ownerId);
        }

        public const string NotYours = "These buttons are for whoever ran the command. Type it yourself for your own.";
    }
}
