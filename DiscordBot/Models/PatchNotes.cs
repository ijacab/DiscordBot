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
            new PatchNote("2026-11-13", "`.networth` takes a time window like `.stocks`: `.nw 48h`, `.nw @person 7d`, `.nw all 24h`."),
            new PatchNote("2026-11-13", "Debt you pay off goes into the jackpot."),
            new PatchNote("2026-11-12", "Debt now grows 1% every hour. Pay it off or turn on `.autosell`."),
            new PatchNote("2026-11-12", "`.portfolio` shows the rent you earn per hour."),
            new PatchNote("2026-11-12", "`.portfolio` groups your houses and apartments into one line each, with the average you paid."),
            new PatchNote("2026-11-12", "The raffle draws every hour among everyone who used money in the hour before, and power-ups go off the moment they're won."),
            new PatchNote("2026-11-12", "Squatters take one property (1st place's best rental), not all their rent."),
            new PatchNote("2026-11-12", "`.autosell on` sells your stocks and then property automatically to pay off debt."),
            new PatchNote("2026-11-12", "The raffle draws every 30 minutes, and lower on the leaderboard you draw more attacks. Every power-up is about twice as strong."),
            new PatchNote("2026-11-12", "Catch-up power-ups that nothing blocks: Tax the Rich, Audit, Bailout, Squatters and Leech. The rich pay in debt if they're short on cash."),
            new PatchNote("2026-11-12", "New power-ups: Mirror, Golden Parachute, Toll Booth, Time Machine, Echo, Copycat, Scratchie, Rigged Raffle, Heist, Musical Chairs, Sticky Fingers and Freeze Ray."),
            new PatchNote("2026-11-11", "Every property is rented out automatically, including ones you already own. `.letout` and `.unlet` are gone."),
            new PatchNote("2026-11-10", "`.lang` mangles harder: 8 hops through far-flung languages like Yoruba, Khmer and Latin."),
            new PatchNote("2026-11-10", "Face Off is reworked: buttons, a live arena with card art, hidden picks, pick your target, a 20 is always best, and spectators can back a fighter."),
            new PatchNote("2026-11-10", "Dungeon has buttons and a live dashboard. You crawl floor by floor, and at each boss door you fight, sneak past or drink a potion. Bosses are on floors 5 and 10 again."),
            new PatchNote("2026-11-10", "The bot runs on .NET 10 and the latest Discord.Net. Nothing should change in play."),
            new PatchNote("2026-11-09", "`max` works for calls, puts, shorts, sells and covers: it buys or sells as much as you can afford, or everything you hold."),
            new PatchNote("2026-11-09", "Rent is paid hourly: 3.5% of a house's market price per hour, 4% for an apartment, before the agency and vacancy."),
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
