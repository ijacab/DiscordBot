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
