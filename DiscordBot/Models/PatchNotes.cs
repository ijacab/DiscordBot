using System.Collections.Generic;

namespace DiscordBot.Models
{
    public class PatchNote
    {
        public PatchNote(string date, string text)
        {
            Date = date;
            Text = text;
        }

        /// <summary>UTC date of the change, yyyy-MM-dd.</summary>
        public string Date { get; }
        public string Text { get; }
    }

    /// <summary>
    /// What changed, for the .patchnotes command. Newest first. Add a one-line entry for each significant change.
    /// </summary>
    public static class PatchNotes
    {
        public static readonly IReadOnlyList<PatchNote> All = new List<PatchNote>
        {
            new PatchNote("2026-11-09", "Rent is paid hourly: $650/h for a house and $520/h for an apartment, gross of the agency and vacancy."),
            new PatchNote("2026-11-09", "Tiers 1 and 2 are safe havens only (bonds and gold). Tiers 3-6 hold the ordinary stocks. Holders of the removed stocks are paid out at current value."),
            new PatchNote("2026-11-09", "`.stocks` lists gold (🥇) and bonds (🏦) in their own Safe havens section, and their graphs say what they are."),
            new PatchNote("2026-11-09", "Rare market crash (about once every 2 years): stocks and property fall for weeks, then slowly recover. Gold rises in a crash, and bonds pay a steady yield."),
            new PatchNote("2026-11-09", "Red Shell, Lightning Bolt, Robin Hood and Hostile Takeover take from players above you. `.pu` shows whether you're in the next raffle."),
            new PatchNote("2026-11-09", "`.portfolio` lists all your assets: cash, stocks, property, debt and total."),
            new PatchNote("2026-11-09", "Property says \"rented out\" instead of \"let out\"."),
            new PatchNote("2026-11-09", "`.help` is split across several messages, so it no longer fails on Discord's size limit."),
            new PatchNote("2026-11-09", "Debt is paid off automatically from any spare cash every minute. `.repay` is removed."),
            new PatchNote("2026-11-09", "Stock and property sales pay debt first. Blue Shell and Pickpocket take cash first, then put any shortfall on debt."),
            new PatchNote("2026-11-09", "Houses and apartments: buy with cash, rent out for daily rent after an agency cut. Prices follow one shared market index."),
            new PatchNote("2026-11-08", "Power-ups that take net worth are no longer capped."),
            new PatchNote("2026-11-08", "5-hourly raffle: anyone who used money in the last 24h is entered and wins a power-up. `.pu` lists and uses them."),
            new PatchNote("2026-11-08", "Crypto stock bursts into moons and crashes, and rebounds after a crash."),
            new PatchNote("2026-11-08", "Shorts, call and put options, one stock per risk tier, and graph time windows."),
            new PatchNote("2026-11-08", "Jackpot: an all-in `max` bet with all your money in cash and no stocks can win the pool."),
            new PatchNote("2026-11-07", "Stock trades move the price, with a 1% spread and a 2% sell tax that goes to the jackpot."),
            new PatchNote("2026-11-06", "Dungeon: blocks of 10 floors with checkpoints, solo runs and PvP.")
        };
    }
}
